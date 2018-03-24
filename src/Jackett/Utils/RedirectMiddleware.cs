using System;
using System.Threading.Tasks;
using Jackett.Common;
using Microsoft.AspNetCore.Http;

namespace Jackett.Utils
{
    public class WebApiRootRedirectMiddleware
    {
        private readonly RequestDelegate _next;

        public WebApiRootRedirectMiddleware(RequestDelegate next)
        {
            //Ideally we'd dependency inject the server config into the middleware but AutoFac's Owin package has not been updated to support Autofac > 5
            _next = next;
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path != null && context.Request.Path.HasValue && context.Request.Path.Value.StartsWith(Engine.ServerConfig.RuntimeSettings.BasePath, StringComparison.Ordinal))
            {
                context.Request.Path = new PathString(context.Request.Path.Value.Substring(Engine.ServerConfig.RuntimeSettings.BasePath.Length));
            }

            if (context.Request.Path == null || string.IsNullOrWhiteSpace(context.Request.Path.ToString()) || context.Request.Path.ToString() == "/")
            {
                // 301 is the status code of permanent redirect
                context.Response.StatusCode = 302;
                var redir = Engine.ServerConfig.RuntimeSettings.BasePath + "/UI/Dashboard";
                Engine.Logger.Info("redirecting to " + redir);
                context.Response.Headers.Add("Location", redir);
                return Task.CompletedTask;
            }
            return _next(context);
        }
    }

    public class LegacyApiRedirectMiddleware
    {
        private readonly RequestDelegate _next;

        public LegacyApiRedirectMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path == null || string.IsNullOrWhiteSpace(context.Request.Path.ToString()) || context.Request.Path.Value.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase))
            {
                // 301 is the status code of permanent redirect
                context.Response.StatusCode = 302;
                var redir = context.Request.Path.Value.Replace("/Admin", "/UI");
                Engine.Logger.Info("redirecting to " + redir);
                context.Response.Headers.Add("Location", redir);
                return Task.CompletedTask;
            }
            return _next.Invoke(context);
        }
    }
}