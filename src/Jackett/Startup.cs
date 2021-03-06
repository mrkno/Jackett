using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Filters;
using Autofac.Integration.WebApi;
using Jackett.Common;
using Jackett.Common.Utils;
using Jackett.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using IExceptionFilter = System.Web.Http.Filters.IExceptionFilter;

namespace Jackett
{
    class ApiExceptionHandler : IExceptionFilter
    {
        public bool AllowMultiple
        {
            get
            {
                return false;
            }
        }

        public Task ExecuteExceptionFilterAsync(HttpActionExecutedContext actionExecutedContext, CancellationToken cancellationToken)
        {
            string msg = "";
            var json = new JObject();
            var exception = actionExecutedContext.Exception;

            Engine.Logger.Error(exception);

            var message = exception.Message;
            if (exception.InnerException != null)
                message += ": " + exception.InnerException.Message;
            msg = message;

            if (exception is ExceptionWithConfigData)
                json["config"] = ((ExceptionWithConfigData)exception).ConfigData.ToJson(null, false);

            json["result"] = "error";
            json["error"] = msg;
            json["stacktrace"] = exception.StackTrace;
            if (exception.InnerException != null)
                json["innerstacktrace"] = exception.InnerException.StackTrace;

            var response = actionExecutedContext.Request.CreateResponse();
            response.Content = new JsonContent(json);
            response.StatusCode = HttpStatusCode.InternalServerError;

            actionExecutedContext.Response = response;

            return Task.FromResult(0);
        }
    }

    static class JackettRouteExtensions
    {
        public static void ConfigureLegacyRoutes(this HttpRouteCollection routeCollection)
        {
            // Sonarr appends /api by default to all Torznab indexers, so we need that "ignored"
            // parameter to catch these as well.

            // Legacy fallback for Torznab results
            routeCollection.MapHttpRoute(
                name: "LegacyTorznab",
                routeTemplate: "torznab/{indexerId}/{ignored}",
                defaults: new
                {
                    controller = "Results",
                    action = "Torznab",
                    ignored = RouteParameter.Optional,
                }
            );

            // Legacy fallback for Potato results
            routeCollection.MapHttpRoute(
                name: "LegacyPotato",
                routeTemplate: "potato/{indexerId}/{ignored}",
                defaults: new
                {
                    controller = "Results",
                    action = "Potato",
                    ignored = RouteParameter.Optional,
                }
            );
        }
    }

    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            services.AddDataProtection();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder appBuilder, IHostingEnvironment env)
        {
            // Configure Web API for self-host. 
            var config = new HttpConfiguration();
            var jackettServerConfig = Engine.ServerConfig;

            // try to fix SocketException crashes
            // based on http://stackoverflow.com/questions/23368885/signalr-owin-self-host-on-linux-mono-socketexception-when-clients-lose-connectio/30583109#30583109
            try
            {
                if (appBuilder.Properties.TryGetValue(typeof(HttpListener).FullName, out object httpListener) && httpListener is HttpListener)
                {
                    // HttpListener should not return exceptions that occur when sending the response to the client
                    ((HttpListener)httpListener).IgnoreWriteExceptions = true;
                    //Engine.Logger.Info("set HttpListener.IgnoreWriteExceptions = true");
                }
            }
            catch (Exception e)
            {
                Engine.Logger.Error(e, "Error while setting HttpListener.IgnoreWriteExceptions = true");
            }
            appBuilder.UseMiddleware<WebApiRootRedirectMiddleware>();
            appBuilder.UseMiddleware<LegacyApiRedirectMiddleware>();

            // register exception handler
            config.Filters.Add(new ApiExceptionHandler());

            ;

            // Setup tracing if enabled
            if (jackettServerConfig.RuntimeSettings.TracingEnabled)
            {
                config.EnableSystemDiagnosticsTracing();
                config.Services.Replace(typeof(ITraceWriter), new WebAPIToNLogTracer(jackettServerConfig));
            }
            // Add request logging if enabled
            if (jackettServerConfig.RuntimeSettings.LogRequests)
                config.MessageHandlers.Add(new WebAPIRequestLogger());

            config.DependencyResolver = new AutofacWebApiDependencyResolver(Engine.GetContainer());


            config.MapHttpAttributeRoutes();

            // Sonarr appends /api by default to all Torznab indexers, so we need that "ignored"
            // parameter to catch these as well.
            // (I'd rather not duplicate the whole route.)
            config.Routes.MapHttpRoute(
                name: "IndexerResultsAPI",
                routeTemplate: "api/v2.0/indexers/{indexerId}/results/{action}/{ignored}",
                defaults: new
                {
                    controller = "Results",
                    action = "Results",
                    ignored = RouteParameter.Optional,
                }
            );

            config.Routes.MapHttpRoute(
                name: "IndexerAPI",
                routeTemplate: "api/v2.0/indexers/{indexerId}/{action}",
                defaults: new
                {
                    controller = "IndexerApi",
                    indexerId = ""
                }
            );
            config.Routes.MapHttpRoute(
                name: "ServerConfiguration",
                routeTemplate: "api/v2.0/server/{action}",
                defaults: new
                {
                    controller = "ServerConfiguration"
                }
            );

            config.Routes.MapHttpRoute(
                name: "WebUI",
                routeTemplate: "UI/{action}",
                defaults: new { controller = "WebUI" }
            );

            config.Routes.MapHttpRoute(
                name: "download",
                routeTemplate: "dl/{indexerID}",
                defaults: new { controller = "Download", action = "Download" }
            );

            config.Routes.MapHttpRoute(
              name: "blackhole",
              routeTemplate: "bh/{indexerID}",
              defaults: new { controller = "Blackhole", action = "Blackhole" }
            );

            config.Routes.ConfigureLegacyRoutes();
            Microsoft.AspNet.WebApi.WebApiAppBuilderExtensions;
            appBuilder.UseWebApi(config);

            appBuilder.UseFileServer(new FileServerOptions
            {
                RequestPath = new PathString(string.Empty),
                FileSystem = new PhysicalFileSystem(Engine.ConfigService.GetContentFolder()),
                EnableDirectoryBrowsing = false,
            });
        }
    }
}
