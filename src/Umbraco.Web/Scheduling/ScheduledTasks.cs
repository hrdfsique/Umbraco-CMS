using System;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Umbraco.Core;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Logging;
using Umbraco.Core.Sync;

namespace Umbraco.Web.Scheduling
{
    //TODO: No scheduled task (i.e. URL) would be secured, so if people are actually using these each task
    // would need to be a publicly available task (URL) which isn't really very good :(
    // We should really be using the AdminTokenAuthorizeAttribute for this stuff

    internal class ScheduledTasks : RecurringTaskBase
    {
        private readonly ApplicationContext _appContext;
        private readonly IUmbracoSettingsSection _settings;
        private static readonly Hashtable ScheduledTaskTimes = new Hashtable();

        public ScheduledTasks(IBackgroundTaskRunner<RecurringTaskBase> runner, int delayMilliseconds, int periodMilliseconds, 
            ApplicationContext appContext, IUmbracoSettingsSection settings)
            : base(runner, delayMilliseconds, periodMilliseconds)
        {
            _appContext = appContext;
            _settings = settings;
        }

        private async Task ProcessTasksAsync(CancellationToken token)
        {
            var scheduledTasks = _settings.ScheduledTasks.Tasks;
            foreach (var t in scheduledTasks)
            {
                var runTask = false;
                if (ScheduledTaskTimes.ContainsKey(t.Alias) == false)
                {
                    runTask = true;
                    ScheduledTaskTimes.Add(t.Alias, DateTime.Now);
                }

                // Add 1 second to timespan to compensate for differencies in timer
                else if (
                    new TimeSpan(
                        DateTime.Now.Ticks - ((DateTime)ScheduledTaskTimes[t.Alias]).Ticks).TotalSeconds + 1 >= t.Interval)
                {
                    runTask = true;
                    ScheduledTaskTimes[t.Alias] = DateTime.Now;
                }

                if (runTask)
                {
                    var taskResult = await GetTaskByHttpAync(t.Url, token);
                    if (t.Log)
                        LogHelper.Info<ScheduledTasks>(string.Format("{0} has been called with response: {1}", t.Alias, taskResult));
                }
            }
        }

        private async Task<bool> GetTaskByHttpAync(string url, CancellationToken token)
        {
            using (var wc = new HttpClient())
            {
                var request = new HttpRequestMessage()
                {
                    RequestUri = new Uri(url),
                    Method = HttpMethod.Get,
                    Content = new StringContent(string.Empty)
                };

                //TODO: pass custom the authorization header, currently these aren't really secured!
                //request.Headers.Authorization = AdminTokenAuthorizeAttribute.GetAuthenticationHeaderValue(_appContext);

                try
                {
                    var result = await wc.SendAsync(request, token);
                    return result.StatusCode == HttpStatusCode.OK;
                }
                catch (Exception ex)
                {
                    LogHelper.Error<ScheduledTasks>("An error occurred calling web task for url: " + url, ex);
                }
                return false;
            }
        }

        public override bool PerformRun()
        {
            throw new NotImplementedException();
        }

        public override async Task<bool> PerformRunAsync(CancellationToken token)
        {
            if (_appContext == null) return true; // repeat...

            if (ServerEnvironmentHelper.GetStatus(_settings) == CurrentServerEnvironmentStatus.Slave)
            {
                LogHelper.Debug<ScheduledTasks>("Does not run on slave servers.");
                return false; // do NOT repeat, server status comes from config and will NOT change
            }

            using (DisposableTimer.DebugDuration<ScheduledTasks>(() => "Scheduled tasks executing", () => "Scheduled tasks complete"))
            {
                try
                {
                    await ProcessTasksAsync(token);
                }
                catch (Exception ee)
                {
                    LogHelper.Error<ScheduledTasks>("Error executing scheduled task", ee);
                }
            }

            return true; // repeat
        }

        public override bool IsAsync
        {
            get { return true; }
        }

        public override bool RunsOnShutdown
        {
            get { return false; }
        }
    }
}