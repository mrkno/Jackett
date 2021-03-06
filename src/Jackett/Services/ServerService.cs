﻿using NLog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections;
using System.Text.RegularExpressions;
using Jackett.Common;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils.Clients;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;

namespace Jackett.Services
{

    public class ServerService : IServerService
    {
        private IDisposable _server = null;

        private IIndexerManagerService indexerService;
        private IProcessService processService;
        private ISerializeService serializeService;
        private IConfigurationService configService;
        private Logger logger;
        private Common.Utils.Clients.WebClient client;
        private IUpdateService updater;
        private List<string> _notices = new List<string>();
        private ServerConfig config;
        IProtectionService _protectionService;

        public ServerService(IIndexerManagerService i, IProcessService p, ISerializeService s, IConfigurationService c, Logger l, Common.Utils.Clients.WebClient w, IUpdateService u, IProtectionService protectionService, ServerConfig serverConfig)
        {
            indexerService = i;
            processService = p;
            serializeService = s;
            configService = c;
            logger = l;
            client = w;
            updater = u;
            config = serverConfig;
            _protectionService = protectionService;
        }        

        public List<string> notices
        {
            get
            {
                return _notices;
            }
        }

        public Uri ConvertToProxyLink(Uri link, string serverUrl, string indexerId, string action = "dl", string file = "t")
        {
            if (link == null || (link.IsAbsoluteUri && link.Scheme == "magnet"))
                return link;

            var encryptedLink = _protectionService.Protect(link.ToString());
            var encodedLink = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(encryptedLink));
            string urlEncodedFile = WebUtility.UrlEncode(file);
            var proxyLink = string.Format("{0}{1}/{2}/?jackett_apikey={3}&path={4}&file={5}", serverUrl, action, indexerId, config.APIKey, encodedLink, urlEncodedFile);
            return new Uri(proxyLink);
        }

        public string BasePath()
        {
            if (config.BasePathOverride == null || config.BasePathOverride == "")
            {
                return "";
            }
            var path = config.BasePathOverride;
            if (path.EndsWith("/"))
            {
                path = path.TrimEnd('/');
            }
            if (!path.StartsWith("/"))
            {
                path = "/" + path;
            }
            return path;
        }
        
        public void Initalize()
        {
            logger.Info("Starting Jackett " + configService.GetVersion());
            try
            {
                var x = Environment.OSVersion;
                var runtimedir = RuntimeEnvironment.GetRuntimeDirectory();
                logger.Info("Environment version: " + Environment.Version.ToString() + " (" + runtimedir + ")");
                logger.Info("OS version: " + Environment.OSVersion.ToString() + (Environment.Is64BitOperatingSystem ? " (64bit OS)" : "") + (Environment.Is64BitProcess ? " (64bit process)" : ""));

                try
                {
                    int workerThreads;
                    int completionPortThreads;
                    ThreadPool.GetMaxThreads(out workerThreads, out completionPortThreads);
                    logger.Info("ThreadPool MaxThreads: " + workerThreads + " workerThreads, " + completionPortThreads + " completionPortThreads");
                }
                catch (Exception e)
                {
                    logger.Error("Error while getting MaxThreads details: " + e);
                }

                try
                {
                    var issuefile = "/etc/issue";
                    if (File.Exists(issuefile))
                    {
                        using (StreamReader reader = new StreamReader(issuefile))
                        {
                            string firstLine;
                            firstLine = reader.ReadLine();
                            if (firstLine != null)
                                logger.Info("issue: " + firstLine);
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error while reading the issue file");
                }

                Type monotype = Type.GetType("Mono.Runtime");
                if (monotype != null)
                {
                    MethodInfo displayName = monotype.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);
                    var monoVersion = "unknown";
                    if (displayName != null)
                        monoVersion = displayName.Invoke(null, null).ToString();
                    logger.Info("mono version: " + monoVersion);

                    var monoVersionO = new Version(monoVersion.Split(' ')[0]);

                    if (monoVersionO.Major < 4)
                    {
                        logger.Error("Your mono version is to old (mono 3 is no longer supported). Please update to the latest version from http://www.mono-project.com/download/");
                        Engine.Exit(2);
                    }
                    else if (monoVersionO.Major == 4 && monoVersionO.Minor == 2)
                    {
                        var notice = "mono version 4.2.* is known to cause problems with Jackett. If you experience any problems please try updating to the latest mono version from http://www.mono-project.com/download/ first.";
                        _notices.Add(notice);
                        logger.Error(notice);
                    }

                    try
                    {
                        // Check for mono-devel
                        // Is there any better way which doesn't involve a hard cashes?
                        var mono_devel_file = Path.Combine(runtimedir, "mono-api-info.exe");
                        if (!File.Exists(mono_devel_file))
                        {
                            var notice = "It looks like the mono-devel package is not installed, please make sure it's installed to avoid crashes.";
                            _notices.Add(notice);
                            logger.Error(notice);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Error while checking for mono-devel");
                    }

                    try
                    {
                        // Check for ca-certificates-mono
                        var mono_cert_file = Path.Combine(runtimedir, "cert-sync.exe");
                        if (!File.Exists(mono_cert_file))
                        {
                            if ((monoVersionO.Major >= 4 && monoVersionO.Minor >= 8) || monoVersionO.Major >= 5)
                            {
                                var notice = "The ca-certificates-mono package is not installed, HTTPS trackers won't work. Please install it.";
                                _notices.Add(notice);
                                logger.Error(notice);
                            }
                            else
                            {
                                logger.Info("The ca-certificates-mono package is not installed, it will become mandatory once mono >= 4.8 is used.");
                            }

                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Error while checking for ca-certificates-mono");
                    }

                    try
                    {
                        Encoding.GetEncoding("windows-1255");
                    }
                    catch (NotSupportedException e)
                    {
                        logger.Debug(e);
                        logger.Error(e.Message + " Most likely the mono-locale-extras package is not installed.");
                        Engine.Exit(2);
                    }

                    if (Engine.WebClientType == typeof(HttpWebClient) || Engine.WebClientType == typeof(HttpWebClient2))
                    { 
                        // check if the certificate store was initialized using Mono.Security.X509.X509StoreManager.TrustedRootCertificates.Count
                        try
                        {
                            var monoSecurity = Assembly.Load("Mono.Security");
                            Type monoX509StoreManager = monoSecurity.GetType("Mono.Security.X509.X509StoreManager");
                            if (monoX509StoreManager != null)
                            {
                                var TrustedRootCertificatesProperty = monoX509StoreManager.GetProperty("TrustedRootCertificates");
                                var TrustedRootCertificates = (ICollection)TrustedRootCertificatesProperty.GetValue(null);

                                logger.Info("TrustedRootCertificates count: " + TrustedRootCertificates.Count);

                                if (TrustedRootCertificates.Count == 0)
                                {
                                    var CACertificatesFiles = new string[] {
                                        "/etc/ssl/certs/ca-certificates.crt", // Debian based
                                        "/etc/pki/tls/certs/ca-bundle.c", // RedHat based
                                        "/etc/ssl/ca-bundle.pem", // SUSE
                                        };

                                    var notice = "The mono certificate store is not initialized.<br/>\n";
                                    var logSpacer = "                     ";
                                    var CACertificatesFile = CACertificatesFiles.Where(f => File.Exists(f)).FirstOrDefault();
                                    var CommandRoot = "curl -sS https://curl.haxx.se/ca/cacert.pem | cert-sync /dev/stdin";
                                    var CommandUser = "curl -sS https://curl.haxx.se/ca/cacert.pem | cert-sync --user /dev/stdin";
                                    if (CACertificatesFile != null)
                                    {
                                        CommandRoot = "cert-sync " + CACertificatesFile;
                                        CommandUser = "cert-sync --user " + CACertificatesFile;
                                    }
                                    notice += logSpacer + "Please run the following command as root:<br/>\n";
                                    notice += logSpacer + "<pre>" + CommandRoot + "</pre><br/>\n";
                                    notice += logSpacer + "If you don't have root access or you're running MacOS, please run the following command as the jackett user (" + Environment.UserName + "):<br/>\n";
                                    notice += logSpacer + "<pre>" + CommandUser + "</pre>";
                                    _notices.Add(notice);
                                    logger.Error(Regex.Replace(notice, "<.*?>", String.Empty));
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, "Error while chekcing the mono certificate store");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error("Error while getting environment details: " + e);
            }

            try
            {
                if (Environment.UserName == "root")
                { 
                    var notice = "Jackett is running with root privileges. You should run Jackett as an unprivileged user.";
                    _notices.Add(notice);
                    logger.Error(notice);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Error while checking the username");
            }

            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
            // Load indexers
            indexerService.InitIndexers(configService.GetCardigannDefinitionsFolders());
            client.Init();
            updater.CleanupTempDir();
        }

        public void Start()
        {
            // Start the server
            logger.Info("Starting web server at " + config.GetListenAddresses()[0]);
            config.RuntimeSettings.BasePath = BasePath();
            try
            {
                var server = WebHost.CreateDefaultBuilder()
                    .UseUrls(config.GetListenAddresses().ToArray())
                    .UseStartup<Startup>()
                    .Build();
                server.Start();
                _server = server;
                //WebApp.Start<Startup>(startOptions);
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException;
                if (inner is SocketException && ((SocketException)inner).SocketErrorCode == SocketError.AddressAlreadyInUse) // Linux (mono)
                {
                    logger.Error("Address already in use: Most likely Jackett is already running.");
                    Engine.Exit(1);
                }
                else if (inner is HttpListenerException && ((HttpListenerException)inner).ErrorCode == 183) // Windows
                {
                    logger.Error(inner.Message + " Most likely Jackett is already running.");
                    Engine.Exit(1);
                }
                throw e;
            }
            logger.Debug("Web server started");
            updater.StartUpdateChecker();
        }

        public void ReserveUrls(bool doInstall = true)
        {
            logger.Debug("Unreserving Urls");
            config.GetListenAddresses(false).ToList().ForEach(u => RunNetSh(string.Format("http delete urlacl {0}", u)));
            config.GetListenAddresses(true).ToList().ForEach(u => RunNetSh(string.Format("http delete urlacl {0}", u)));
            if (doInstall)
            {
                logger.Debug("Reserving Urls");
                config.GetListenAddresses(config.AllowExternal).ToList().ForEach(u => RunNetSh(string.Format("http add urlacl {0} sddl=D:(A;;GX;;;S-1-1-0)", u)));
                logger.Debug("Urls reserved");
            }
        }

        private void RunNetSh(string args)
        {
            processService.StartProcessAndLog("netsh.exe", args);
        }

        public void Stop()
        {
            if (_server != null)
            {
                _server.Dispose();
            }
        }

        public string GetServerUrl(HttpRequestMessage Request)
        {
            var scheme = Request.RequestUri.Scheme;
            var port = Request.RequestUri.Port;

            // Check for protocol headers added by reverse proxys
            // X-Forwarded-Proto: A de facto standard for identifying the originating protocol of an HTTP request
            var X_Forwarded_Proto = Request.Headers.Where(x => x.Key == "X-Forwarded-Proto").Select(x => x.Value).FirstOrDefault();
            if (X_Forwarded_Proto != null)
            {
                scheme = X_Forwarded_Proto.First();
            }
            // Front-End-Https: Non-standard header field used by Microsoft applications and load-balancers
            else if (Request.Headers.Where(x => x.Key == "Front-End-Https" && x.Value.FirstOrDefault() == "on").Any())
            {
                scheme = "https";
            }

            // default to 443 if the Host header doesn't contain the port (needed for reverse proxy setups)
            if (scheme == "https" && !Request.RequestUri.Authority.Contains(":"))
                port = 443;

            var serverUrl = string.Format("{0}://{1}:{2}{3}/", scheme, Request.RequestUri.Host, port, BasePath());
            return serverUrl;
        }
    }
}
