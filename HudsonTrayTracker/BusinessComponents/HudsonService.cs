using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Xml;
using Hudson.TrayTracker.Entities;
using Hudson.TrayTracker.Utils;
using Common.Logging;
using System.Reflection;
using Hudson.TrayTracker.Utils.Logging;
using Iesi.Collections.Generic;
using Hudson.TrayTracker.Utils.Web;
using System.Threading;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace Hudson.TrayTracker.BusinessComponents
{
    public class HudsonService
    {
        public static readonly String buildDetailsFilter = "?tree=number,timestamp,builtOn,culprits[fullName],changeSet[items[*,author[id]]]";
        static readonly ILog logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        [ThreadStatic]
        static WebClient threadWebClient;

        [ThreadStatic]
        static bool ignoreUntrustedCertificate;

        // cache: key=url, value=xml
        IDictionary<string, string> cache = new Dictionary<string, string>();
        // URLs visited between 2 calls to RecycleCache()
        ISet<string> visitedURLs = new HashedSet<string>();

        public ClaimService ClaimService { get; set; }

        public HudsonService()
        {
            ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;
        }

        public IList<Project> LoadProjects(Server server)
        {
            String url = NetUtils.ConcatUrls(server.Url, "/api/xml");

            logger.Info("Loading projects from " + url);

            String xmlStr = DownloadString(server.Credentials, url, false, server.IgnoreUntrustedCertificate);

            if (logger.IsTraceEnabled)
                logger.Trace("XML: " + xmlStr);

            XmlDocument xml = new XmlDocument();
            xml.LoadXml(xmlStr);

            XmlNodeList jobElements = xml.SelectNodes("/hudson/job");
            var projects = new List<Project>();
            foreach (XmlNode jobElement in jobElements)
            {
                string projectName = jobElement.SelectSingleNode("name").InnerText;
                string projectUrl = jobElement.SelectSingleNode("url").InnerText;

                Project project = new Project();
                project.Server = server;
                project.Name = projectName;
                project.Url = projectUrl;

                if (logger.IsDebugEnabled)
                    logger.Debug("Found project " + projectName + " (" + projectUrl + ")");

                projects.Add(project);
            }

            logger.Info("Done loading projects");

            return projects;
        }

        public AllBuildDetails UpdateProject(Project project)
        {
            String url = NetUtils.ConcatUrls(project.Url, "/api/xml");

            logger.Info("Updating project from " + url);

            Credentials credentials = project.Server.Credentials;
            bool ignoreUntrustedCertificate = project.Server.IgnoreUntrustedCertificate;
            String xmlStr = DownloadString(credentials, url, false, ignoreUntrustedCertificate);

            if (logger.IsTraceEnabled)
                logger.Trace("XML: " + xmlStr);

            XmlDocument xml = new XmlDocument();
            xml.LoadXml(xmlStr);

            string status = xml.SelectSingleNode("/*/color").InnerText;
            string lastCompletedBuildUrl = XmlUtils.SelectSingleNodeText(xml, "/*/lastCompletedBuild/url");
            string lastSuccessfulBuildUrl = XmlUtils.SelectSingleNodeText(xml, "/*/lastSuccessfulBuild/url");
            string lastFailedBuildUrl = XmlUtils.SelectSingleNodeText(xml, "/*/lastFailedBuild/url");
            bool? stuck = XmlUtils.SelectSingleNodeBoolean(xml, "/*/queueItem/stuck");

            AllBuildDetails res = new AllBuildDetails();
            res.Status = GetStatus(status, stuck);
            res.LastCompletedBuild = GetBuildDetails(credentials, lastCompletedBuildUrl, ignoreUntrustedCertificate);
            res.LastSuccessfulBuild = GetBuildDetails(credentials, lastSuccessfulBuildUrl, ignoreUntrustedCertificate);
            res.LastFailedBuild = GetBuildDetails(credentials, lastFailedBuildUrl, ignoreUntrustedCertificate);

            logger.Info("Done updating project");
            return res;
        }

        private BuildStatus GetStatus(string status, bool? stuck)
        {
            BuildStatusEnum value;
            if (status.StartsWith("grey"))
                value = BuildStatusEnum.Indeterminate;
            else if (status.StartsWith("blue") || status.StartsWith("green"))
                value = BuildStatusEnum.Successful;
            else if (status.StartsWith("yellow"))
                value = BuildStatusEnum.Unstable;
            else if (status.StartsWith("red"))
                value = BuildStatusEnum.Failed;
            else if (status.StartsWith("aborted"))
                value = BuildStatusEnum.Aborted;
            else
                value = BuildStatusEnum.Unknown;

            bool isInProgress = status.EndsWith("_anime");
            bool isStuck = (stuck.HasValue && stuck.Value == true);
            return new BuildStatus(value, isInProgress, isStuck);
        }

        private BuildDetails GetBuildDetails(Credentials credentials, string buildUrl, bool ignoreUntrustedCertificate)
        {
            if (buildUrl == null)
                return null;

            String url = NetUtils.ConcatUrls(buildUrl, "/api/xml" , buildDetailsFilter);
            BuildDetails res = new BuildDetails();

            if (logger.IsDebugEnabled)
                logger.Debug("Getting build details from " + url);

            String xmlStr = DownloadString(credentials, url, true, ignoreUntrustedCertificate);

            if (logger.IsTraceEnabled)
                logger.Trace("XML: " + xmlStr);

            XmlDocument xml = new XmlDocument();
            xml.LoadXml(xmlStr);

            string number = xml.SelectSingleNode("/*/number").InnerText;
            string timestamp = xml.SelectSingleNode("/*/timestamp").InnerText;
            XmlNodeList userNodes = xml.SelectNodes("/*/culprit/fullName");
            XmlNodeList changeNodes = xml.SelectNodes("/*/changeSet/item");

            TimeSpan ts = TimeSpan.FromSeconds(long.Parse(timestamp) / 1000);
            DateTime date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            date = date.Add(ts);
           
            int count = changeNodes.Count;
            if (count == 0)
            {
                // No changes were introduced;
                res.FailureReason = "Server Issue / Other";
            }
            else
            {
                // New changeset casued broken build: retrieve a name of the last committer;
                XmlNode committerNode = changeNodes.Item(count - 1);
                XmlNodeList childNodes = committerNode.ChildNodes;
                foreach (XmlNode node in childNodes)
                {
                    if (node.Name == "author")
                        res.CommitterName = node.LastChild.InnerText;
                }
            }

            ISet<string> users = new HashedSet<string>();
            foreach (XmlNode userNode in userNodes)
            {
                string userName = StringUtils.ExtractUserName(userNode.InnerText);
                users.Add(userName);
            }

            res.Number = int.Parse(number);
            res.Time = date;
            res.Users = users;

            ClaimService.FillInBuildDetails(res, xml);

            if (logger.IsDebugEnabled)
                logger.Debug("Done getting build details");

            return res;
        }

        public void SafeRunBuild(Project project)
        {
            try
            {
                RunBuild(project);
            }
            catch (Exception ex)
            {
                LoggingHelper.LogError(logger, ex);
            }
        }

        public void RunBuild(Project project)
        {
            String url = NetUtils.ConcatUrls(project.Url, "/build?delay=0sec");

            logger.Info("Running build at " + url);

            Credentials credentials = project.Server.Credentials;
            String str = DownloadString(credentials, url, false, project.Server.IgnoreUntrustedCertificate);

            if (logger.IsTraceEnabled)
                logger.Trace("Result: " + str);

            logger.Info("Done running build");
        }

        private String DownloadString(Credentials credentials, string url, bool cacheable,
            bool ignoreUntrustedCertificate)
        {
            string res;

            if (logger.IsTraceEnabled)
                logger.Trace("Downloading: " + url);

            if (cacheable)
            {
                lock (this)
                {
                    // mark the URL as visited
                    visitedURLs.Add(url);
                    // perform a lookup in the cache
                    if (cache.TryGetValue(url, out res))
                    {
                        if (logger.IsTraceEnabled)
                            logger.Trace("Cache hit: " + url);
                        return res;
                    }
                }

                if (logger.IsTraceEnabled)
                    logger.Trace("Cache miss: " + url);
            }

            // set the thread-static field
            HudsonService.ignoreUntrustedCertificate = ignoreUntrustedCertificate;

            WebClient webClient = GetWebClient(credentials);
            res = webClient.DownloadString(url);

            if (logger.IsTraceEnabled)
                logger.Trace("Downloaded: " + res);

            if (cacheable)
            {
                lock (this)
                {
                    // store in cache
                    cache[url] = res;
                }
            }

            return res;
        }

        private WebClient GetWebClient(Credentials credentials)
        {
            if (threadWebClient == null)
            {
                logger.Info("Creating web client in thread " + Thread.CurrentThread.ManagedThreadId
                    + " (" + Thread.CurrentThread.Name + ")");
                threadWebClient = new CookieAwareWebClient();
                threadWebClient.Encoding = Encoding.UTF8;
            }

            // reinitialize HTTP headers
            threadWebClient.Headers = new WebHeaderCollection();

            // credentials
            if (credentials != null)
            {
                string authentication = "Basic " + Convert.ToBase64String(
                    Encoding.ASCII.GetBytes(credentials.Username + ":" + credentials.Password));
                threadWebClient.Headers.Add("Authorization", authentication);
            }

            return threadWebClient;
        }

        public void RecycleCache()
        {
            lock (this)
            {
                if (logger.IsTraceEnabled)
                    logger.Trace("Recycling cache: " + cache.Keys.Count + " items in cache");

                var newCache = new Dictionary<string, string>();

                foreach (string visitedURL in visitedURLs)
                {
                    string value;
                    if (cache.TryGetValue(visitedURL, out value))
                        newCache.Add(visitedURL, value);
                }

                cache = newCache;
                visitedURLs.Clear();

                if (logger.IsTraceEnabled)
                    logger.Trace("Recycling cache: " + cache.Keys.Count + " items in cache");
            }
        }

        public void RemoveFromCache(string url)
        {
            lock (this)
            {
                cache.Remove(url);
            }
        }

        public string GetConsolePage(Project project)
        {
            string res = project.Url;
            bool hasBuild = HasBuild(project.AllBuildDetails);
            if (hasBuild)
                res += "lastBuild/console";
            return res;
        }

        private bool HasBuild(AllBuildDetails allBuildDetails)
        {
            // no details, there is no build
            if (allBuildDetails == null)
                return false;
            // if there is a completed build, there is a build
            if (allBuildDetails.LastCompletedBuild != null)
                return true;
            // if there is a build in progress, there is a build
            bool buildInProgress = allBuildDetails.Status.IsInProgress;
            return buildInProgress;
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate,
            X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (ignoreUntrustedCertificate == true)
                return true;
            return sslPolicyErrors == SslPolicyErrors.None;
        }
    }
}
