using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.DataHelper;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using TVHeadEnd.TimeoutHelper;

namespace TVHeadEnd
{
    /// <summary>
    /// Class LiveTvService
    /// </summary>
    public class LiveTvService : ILiveTvService, HTSConnectionListener
    {
        private readonly TimeSpan TIMEOUT = TimeSpan.FromMinutes(5);

        private volatile Boolean _connected = false;
        private volatile Boolean _initialLoadFinished = false;
        private volatile int _subscriptionId = 0;

        private readonly ILogger _logger;

        private HTSConnectionAsync _htsConnection;
        private int _priority;
        private string _profile;
        private string _httpBaseUrl;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly TunerDataHelper _tunerDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        public LiveTvService(ILogger logger)
        {
            _logger = logger;

            _tunerDataHelper = new TunerDataHelper(logger);
            _channelDataHelper = new ChannelDataHelper(logger, _tunerDataHelper);
            _dvrDataHelper = new DvrDataHelper(logger);
            _autorecDataHelper = new AutorecDataHelper(logger);

            createHTSConnection();
        }

        private void createHTSConnection()
        {
            _logger.Info("[TVHclient] LiveTvService.createHTSConnection()");
            Version version = Assembly.GetEntryAssembly().GetName().Version;
            _htsConnection = new HTSConnectionAsync(this, "TVHclient", version.ToString(), _logger);
            _connected = false;
        }

        private void ensureConnection()
        {
            if (_htsConnection == null)
            {
                return;
            }

            if (_htsConnection.needsRestart())
            {
                createHTSConnection();
            }

            lock (_htsConnection)
            {
                if (!_connected)
                {
                    var config = Plugin.Instance.Configuration;

                    if (string.IsNullOrEmpty(config.TVH_ServerName))
                    {
                        string message = "[TVHclient] LiveTvService.ensureConnection: TVH-Server name must be configured.";
                        _logger.Error(message);
                        throw new InvalidOperationException(message);
                    }

                    if (string.IsNullOrEmpty(config.Username))
                    {
                        string message = "[TVHclient] LiveTvService.ensureConnection: Username must be configured.";
                        _logger.Error(message);
                        throw new InvalidOperationException(message);
                    }

                    if (string.IsNullOrEmpty(config.Password))
                    {
                        string message = "[TVHclient] LiveTvService.ensureConnection: Password must be configured.";
                        _logger.Error(message);
                        throw new InvalidOperationException(message);
                    }

                    _priority = config.Priority;
                    _profile = config.Profile;

                    if (_priority < 0 || _priority > 4)
                    {
                        _priority = 2;
                        _logger.Info("[TVHclient] LiveTvService.ensureConnection: Priority was out of range [0-4] - set to 2");
                    }

                    _httpBaseUrl = "http://" + config.TVH_ServerName + ":" + config.HTTP_Port;

                    _htsConnection.open(config.TVH_ServerName, config.HTSP_Port);
                    _connected = _htsConnection.authenticate(config.Username, config.Password);

                    _logger.Info("[TVHclient] LiveTvService.ensureConnection: connection established " + _connected);

                    _channelDataHelper.clean();
                    _dvrDataHelper.clean();
                    _autorecDataHelper.clean();
                }
            }
        }


        /// <summary>
        /// Gets the channels async.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IEnumerable{ChannelInfo}}.</returns>
        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] GetChannelsAsync, call canceled or timed out - returning empty list.");
                return new List<ChannelInfo>();
            }

            //IEnumerable<ChannelInfo> data = await _channelDataHelper.buildChannelInfos(cancellationToken);
            //return data;

            TaskWithTimeoutRunner<IEnumerable<ChannelInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ChannelInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<ChannelInfo>> twtRes = await 
                twtr.RunWithTimeout(_channelDataHelper.buildChannelInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<ChannelInfo>();
            }

            return twtRes.Result;
        }

        private Task<int> WaitForInitialLoadTask(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<int>(() =>
            {
                DateTime start = DateTime.Now;
                while (!_initialLoadFinished || cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(500);
                    TimeSpan duration = DateTime.Now - start;
                    long durationInSec = duration.Ticks / TimeSpan.TicksPerSecond;
                    if (durationInSec > 60 * 15) // 15 Min timeout, should be enough to load huge data count
                    {
                        return -1;
                    }
                }
                return 0;
            });
        }

        /// <summary>
        /// Gets the Recordings async
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IEnumerable{RecordingInfo}}</returns>
        public async Task<IEnumerable<RecordingInfo>> GetRecordingsAsync(CancellationToken cancellationToken)
        {
            // retrieve all 'Pending', 'Inprogress' and 'Completed' recordings
            // we don't deliver the 'Pending' recordings

            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] GetRecordingsAsync, call canceled or timed out - returning empty list.");
                return new List<RecordingInfo>();
            }

            //IEnumerable<RecordingInfo> data = await _dvrDataHelper.buildDvrInfos(cancellationToken);
            //return data;

            TaskWithTimeoutRunner<IEnumerable<RecordingInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<RecordingInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<RecordingInfo>> twtRes = await 
                twtr.RunWithTimeout(_dvrDataHelper.buildDvrInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<RecordingInfo>();
            }

            return twtRes.Result;
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public string Name
        {
            get { return "TVHclient"; }
        }

        /// <summary>
        /// Delete the Recording async from the disk
        /// </summary>
        /// <param name="recordingId">The recordingId</param>
        /// <param name="cancellationToken">The cancellationToken</param>
        /// <returns></returns>
        public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] DeleteRecordingAsync, call canceled or timed out.");
                return;
            }

            HTSMessage deleteRecordingMessage = new HTSMessage();
            deleteRecordingMessage.Method = "deleteDvrEntry";
            deleteRecordingMessage.putField("id", recordingId);

            //HTSMessage deleteRecordingResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(deleteRecordingMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnection.sendMessage(deleteRecordingMessage, lbrh);
                    return lbrh.getResponse();
                }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't delete recording because of timeout");
            }
            else
            {
                HTSMessage deleteRecordingResponse = twtRes.Result;
                Boolean success = deleteRecordingResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    _logger.Error("[TVHclient] Can't delete recording: '" + deleteRecordingResponse.getString("error") + "'");
                }
            }
        }

        /// <summary>
        /// Cancel pending scheduled Recording 
        /// </summary>
        /// <param name="timerId">The timerId</param>
        /// <param name="cancellationToken">The cancellationToken</param>
        /// <returns></returns>
        public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] CancelTimerAsync, call canceled or timed out.");
                return;
            }

            HTSMessage cancelTimerMessage = new HTSMessage();
            cancelTimerMessage.Method = "cancelDvrEntry";
            cancelTimerMessage.putField("id", timerId);

            //HTSMessage cancelTimerResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(cancelTimerMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage > (() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnection.sendMessage(cancelTimerMessage, lbrh);
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't cancel timer because of timeout");
            }
            else
            {
                HTSMessage cancelTimerResponse = twtRes.Result;
                Boolean success = cancelTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    _logger.Error("[TVHclient] Can't cancel timer: '" + cancelTimerResponse.getString("error") + "'");
                }
            }
        }

        /// <summary>
        /// Create a new recording
        /// </summary>
        /// <param name="info">The TimerInfo</param>
        /// <param name="cancellationToken">The cancellationToken</param>
        /// <returns></returns>
        public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] CreateTimerAsync, call canceled or timed out.");
                return;
            }

            HTSMessage createTimerMessage = new HTSMessage();
            createTimerMessage.Method = "addDvrEntry";
            createTimerMessage.putField("channelId", info.ChannelId);
            createTimerMessage.putField("start", DateTimeHelper.getUnixUTCTimeFromUtcDateTime(info.StartDate));
            createTimerMessage.putField("stop", DateTimeHelper.getUnixUTCTimeFromUtcDateTime(info.EndDate));
            createTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            createTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            createTimerMessage.putField("priority", _priority); // info.Priority delivers always 0 - no GUI
            createTimerMessage.putField("configName", _profile);
            createTimerMessage.putField("description", info.Overview);
            createTimerMessage.putField("title", info.Name);
            createTimerMessage.putField("creator", Plugin.Instance.Configuration.Username);

            //HTSMessage createTimerResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(createTimerMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnection.sendMessage(createTimerMessage, lbrh);
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't create timer because of timeout");
            }
            else
            {
                HTSMessage createTimerResponse = twtRes.Result;
                Boolean success = createTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    _logger.Error("[TVHclient] Can't create timer: '" + createTimerResponse.getString("error") + "'");
                }
            }
        }

        /// <summary>
        /// Update a single Timer
        /// </summary>
        /// <param name="info">The program info</param>
        /// <param name="cancellationToken">The CancellationToken</param>
        /// <returns></returns>
        public async Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] UpdateTimerAsync, call canceled or timed out.");
                return;
            }

            HTSMessage updateTimerMessage = new HTSMessage();
            updateTimerMessage.Method = "updateDvrEntry";
            updateTimerMessage.putField("id", info.Id);
            updateTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            updateTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));

            //HTSMessage updateTimerResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(updateTimerMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            {
                LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                _htsConnection.sendMessage(updateTimerMessage, lbrh);
                return lbrh.getResponse();
            }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't update timer because of timeout");
            }
            else
            {
                HTSMessage updateTimerResponse = twtRes.Result;
                Boolean success = updateTimerResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    _logger.Error("[TVHclient] Can't update timer: '" + updateTimerResponse.getString("error") + "'");
                }
            }
        }

        /// <summary>
        /// Get the pending Recordings.
        /// </summary>
        /// <param name="cancellationToken">The CancellationToken</param>
        /// <returns></returns>
        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        {
            //  retrieve the 'Pending' recordings");

            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] GetTimersAsync, call canceled or timed out - returning empty list.");
                return new List<TimerInfo>();
            }

            //IEnumerable<TimerInfo> data = await _dvrDataHelper.buildPendingTimersInfos(cancellationToken);
            //return data;

            TaskWithTimeoutRunner<IEnumerable<TimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<TimerInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<TimerInfo>> twtRes = await 
                twtr.RunWithTimeout(_dvrDataHelper.buildPendingTimersInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<TimerInfo>();
            }

            return twtRes.Result;
        }

        /// <summary>
        /// Get the recurrent recordings
        /// </summary>
        /// <param name="cancellationToken">The CancellationToken</param>
        /// <returns></returns>
        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] GetSeriesTimersAsync, call canceled ot timed out - returning empty list.");
                return new List<SeriesTimerInfo>();
            }

            //IEnumerable<SeriesTimerInfo> data = await _autorecDataHelper.buildAutorecInfos(cancellationToken);
            //return data;

            TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<SeriesTimerInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<SeriesTimerInfo>> twtRes = await 
                twtr.RunWithTimeout(_autorecDataHelper.buildAutorecInfos(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<SeriesTimerInfo>();
            }

            return twtRes.Result;
        }

        /// <summary>
        /// Create a recurrent recording
        /// </summary>
        /// <param name="info">The recurrend program info</param>
        /// <param name="cancellationToken">The CancelationToken</param>
        /// <returns></returns>
        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            // Dummy method to avoid warnings
            await Task.Factory.StartNew<int>(() => { return 0; });

            throw new NotImplementedException();

            //ensureConnection();

            //int timeOut = await WaitForInitialLoadTask(cancellationToken);
            //if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            //{
            //    _logger.Info("[TVHclient] CreateSeriesTimerAsync, call canceled or timed out - returning empty list.");
            //    return;
            //}

            ////_logger.Info("[TVHclient] CreateSeriesTimerAsync: got SeriesTimerInfo: " + dump(info));

            //HTSMessage createSeriesTimerMessage = new HTSMessage();
            //createSeriesTimerMessage.Method = "addAutorecEntry";
            //createSeriesTimerMessage.putField("title", info.Name);
            //if (!info.RecordAnyChannel)
            //{
            //    createSeriesTimerMessage.putField("channelId", info.ChannelId);
            //}
            //createSeriesTimerMessage.putField("minDuration", 0);
            //createSeriesTimerMessage.putField("maxDuration", 0);

            //int tempPriority = info.Priority;
            //if (tempPriority == 0)
            //{
            //    tempPriority = _priority; // info.Priority delivers 0 if timers is newly created - no GUI
            //}
            //createSeriesTimerMessage.putField("priority", tempPriority);
            //createSeriesTimerMessage.putField("configName", _profile);
            //createSeriesTimerMessage.putField("daysOfWeek", AutorecDataHelper.getDaysOfWeekFromList(info.Days));

            //if (!info.RecordAnyTime)
            //{
            //    createSeriesTimerMessage.putField("approxTime", AutorecDataHelper.getMinutesFromMidnight(info.StartDate));
            //}
            //createSeriesTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60L));
            //createSeriesTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60L));
            //createSeriesTimerMessage.putField("comment", info.Overview);


            ////_logger.Info("[TVHclient] CreateSeriesTimerAsync: created HTSP message: " + createSeriesTimerMessage.ToString());


            ///*
            //        public DateTime EndDate { get; set; }
            //        public string ProgramId { get; set; }
            //        public bool RecordNewOnly { get; set; } 
            // */

            ////HTSMessage createSeriesTimerResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            ////{
            ////    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            ////    _htsConnection.sendMessage(createSeriesTimerMessage, lbrh);
            ////    return lbrh.getResponse();
            ////});

            //TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            //TaskWithTimeoutResult<HTSMessage> twtRes = await  twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(createSeriesTimerMessage, lbrh);
            //    return lbrh.getResponse();
            //}));

            //if (twtRes.HasTimeout)
            //{
            //    _logger.Error("[TVHclient] Can't create series because of timeout");
            //}
            //else
            //{
            //    HTSMessage createSeriesTimerResponse = twtRes.Result;
            //    Boolean success = createSeriesTimerResponse.getInt("success", 0) == 1;
            //    if (!success)
            //    {
            //        _logger.Error("[TVHclient] Can't create series timer: '" + createSeriesTimerResponse.getString("error") + "'");
            //    }
            //}
        }

        private string dump(SeriesTimerInfo sti)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n<SeriesTimerInfo>\n");
            sb.Append("  Id:                    " + sti.Id + "\n");
            sb.Append("  Name:                  " + sti.Name + "\n");
            sb.Append("  Overview:              " + sti.Overview + "\n");
            sb.Append("  Priority:              " + sti.Priority + "\n");
            sb.Append("  ChannelId:             " + sti.ChannelId + "\n");
            sb.Append("  ProgramId:             " + sti.ProgramId + "\n");
            sb.Append("  Days:                  " + dump(sti.Days) + "\n");
            sb.Append("  StartDate:             " + sti.StartDate + "\n");
            sb.Append("  EndDate:               " + sti.EndDate + "\n");
            sb.Append("  IsPrePaddingRequired:  " + sti.IsPrePaddingRequired + "\n");
            sb.Append("  PrePaddingSeconds:     " + sti.PrePaddingSeconds + "\n");
            sb.Append("  IsPostPaddingRequired: " + sti.IsPrePaddingRequired + "\n");
            sb.Append("  PostPaddingSeconds:    " + sti.PostPaddingSeconds + "\n");
            sb.Append("  RecordAnyChannel:      " + sti.RecordAnyChannel + "\n");
            sb.Append("  RecordAnyTime:         " + sti.RecordAnyTime + "\n");
            sb.Append("  RecordNewOnly:         " + sti.RecordNewOnly + "\n");
            sb.Append("</SeriesTimerInfo>\n");
            return sb.ToString();
        }

        private string dump(List<DayOfWeek> days)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DayOfWeek dow in days)
            {
                sb.Append(dow + ", ");
            }
            string tmpResult = sb.ToString();
            if (tmpResult.EndsWith(","))
            {
                tmpResult = tmpResult.Substring(0, tmpResult.Length - 2);
            }
            return tmpResult;
        }

        /// <summary>
        /// Update the series Timer
        /// </summary>
        /// <param name="info">The series program info</param>
        /// <param name="cancellationToken">The CancellationToken</param>
        /// <returns></returns>
        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            // Dummy method to avoid warnings
            await Task.Factory.StartNew<int>(() => { return 0; });

            throw new NotImplementedException();

            //await CancelSeriesTimerAsync(info.Id, cancellationToken);
            //await CreateSeriesTimerAsync(info, cancellationToken);
        }

        /// <summary>
        /// Cancel the Series Timer
        /// </summary>
        /// <param name="timerId">The Timer Id</param>
        /// <param name="cancellationToken">The CancellationToken</param>
        /// <returns></returns>
        public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] CancelSeriesTimerAsync, call canceled or timed out.");
                return;
            }

            HTSMessage deleteAutorecMessage = new HTSMessage();
            deleteAutorecMessage.Method = "deleteAutorecEntry";
            deleteAutorecMessage.putField("id", timerId);

            //HTSMessage deleteAutorecResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(deleteAutorecMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnection.sendMessage(deleteAutorecMessage, lbrh);
                    return lbrh.getResponse();
                }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't delete recording because of timeout");
            }
            else
            {
                HTSMessage deleteAutorecResponse = twtRes.Result;
                Boolean success = deleteAutorecResponse.getInt("success", 0) == 1;
                if (!success)
                {
                    _logger.Error("[TVHclient] Can't cancel timer: '" + deleteAutorecResponse.getString("error") + "'");
                }
            }
        }

        public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<List<MediaSourceInfo>> GetRecordingStreamMediaSources(string recordingId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<MediaSourceInfo> GetChannelStream(string channelId, string mediaSourceId, CancellationToken cancellationToken)
        {
            ensureConnection();

            HTSMessage getTicketMessage = new HTSMessage();
            getTicketMessage.Method = "getTicket";
            getTicketMessage.putField("channelId", channelId);

            //HTSMessage getTicketResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(getTicketMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnection.sendMessage(getTicketMessage, lbrh);
                    return lbrh.getResponse();
                }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't delete recording because of timeout");
            }
            else
            {
                HTSMessage getTicketResponse = twtRes.Result;

                if (_subscriptionId == int.MaxValue)
                {
                    _subscriptionId = 0;
                }
                int currSubscriptionId = _subscriptionId++;

                return new MediaSourceInfo
                {
                    Id = "" + currSubscriptionId,
                    Path = _httpBaseUrl + getTicketResponse.getString("path") + "?ticket=" + getTicketResponse.getString("ticket"),
                    Protocol = MediaProtocol.Http,
                    MediaStreams = new List<MediaStream>
                        {
                            new MediaStream
                            {
                                Type = MediaStreamType.Video,
                                // Set the index to -1 because we don't know the exact index of the video stream within the container
                                Index = -1,
                                // Set to true if unknown to enable deinterlacing
                                IsInterlaced = true
                            },
                            new MediaStream
                            {
                                Type = MediaStreamType.Audio,
                                // Set the index to -1 because we don't know the exact index of the audio stream within the container
                                Index = -1
                            }
                        }
                };
            }

            throw new TimeoutException("");
        }

        public async Task<MediaSourceInfo> GetRecordingStream(string recordingId, string mediaSourceId, CancellationToken cancellationToken)
        {
            ensureConnection();

            HTSMessage getTicketMessage = new HTSMessage();
            getTicketMessage.Method = "getTicket";
            getTicketMessage.putField("dvrId", recordingId);

            //HTSMessage getTicketResponse = await Task.Factory.StartNew<HTSMessage>(() =>
            //{
            //    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
            //    _htsConnection.sendMessage(getTicketMessage, lbrh);
            //    return lbrh.getResponse();
            //});

            TaskWithTimeoutRunner<HTSMessage> twtr = new TaskWithTimeoutRunner<HTSMessage>(TIMEOUT);
            TaskWithTimeoutResult<HTSMessage> twtRes = await  twtr.RunWithTimeout(Task.Factory.StartNew<HTSMessage>(() =>
                {
                    LoopBackResponseHandler lbrh = new LoopBackResponseHandler();
                    _htsConnection.sendMessage(getTicketMessage, lbrh);
                    return lbrh.getResponse();
                }));

            if (twtRes.HasTimeout)
            {
                _logger.Error("[TVHclient] Can't delete recording because of timeout");
            }
            else
            {
                HTSMessage getTicketResponse = twtRes.Result;

                if (_subscriptionId == int.MaxValue)
                {
                    _subscriptionId = 0;
                }
                int currSubscriptionId = _subscriptionId++;

                return new MediaSourceInfo
                {
                    Id = "" + currSubscriptionId,
                    Path = _httpBaseUrl + getTicketResponse.getString("path") + "?ticket=" + getTicketResponse.getString("ticket"),
                    Protocol = MediaProtocol.Http,
                    MediaStreams = new List<MediaStream>
                        {
                            new MediaStream
                            {
                                Type = MediaStreamType.Video,
                                // Set the index to -1 because we don't know the exact index of the video stream within the container
                                Index = -1,
                                // Set to true if unknown to enable deinterlacing
                                IsInterlaced = true
                            },
                            new MediaStream
                            {
                                Type = MediaStreamType.Audio,
                                // Set the index to -1 because we don't know the exact index of the audio stream within the container
                                Index = -1
                            }
                        }
                };
            }

            throw new TimeoutException();
        }

        public async Task CloseLiveStream(string subscriptionId, CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew<string>(() =>
            {
                //_logger.Info("[TVHclient] CloseLiveStream for subscriptionId = " + subscriptionId);
                return subscriptionId;
            });
        }

        public async Task CopyFilesAsync(StreamReader source, StreamWriter destination)
        {
            char[] buffer = new char[0x1000];
            int numRead;
            while ((numRead = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await destination.WriteAsync(buffer, 0, numRead);
            }
        }

        public async Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program = null)
        {
            return await Task.Factory.StartNew<SeriesTimerInfo>(() =>
            {
                return new SeriesTimerInfo
                {
                    PostPaddingSeconds = 0,
                    PrePaddingSeconds = 0,
                    RecordAnyChannel = true,
                    RecordAnyTime = true,
                    RecordNewOnly = false
                };
            });
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] GetProgramsAsync, call canceled or timed out - returning empty list.");
                return new List<ProgramInfo>();
            }

            GetEventsResponseHandler currGetEventsResponseHandler = new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger, cancellationToken);

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.putField("channelId", Convert.ToInt32(channelId));
            _htsConnection.sendMessage(queryEvents, currGetEventsResponseHandler);

            //IEnumerable<ProgramInfo> pi = await currGetEventsResponseHandler.GetEvents(cancellationToken);
            //return pi;

            TaskWithTimeoutRunner<IEnumerable<ProgramInfo>> twtr = new TaskWithTimeoutRunner<IEnumerable<ProgramInfo>>(TIMEOUT);
            TaskWithTimeoutResult<IEnumerable<ProgramInfo>> twtRes = await 
                twtr.RunWithTimeout(currGetEventsResponseHandler.GetEvents(cancellationToken));

            if (twtRes.HasTimeout)
            {
                return new List<ProgramInfo>();
            }

            return twtRes.Result;
        }

        public Task RecordLiveStream(string id, CancellationToken cancellationToken)
        {
            _logger.Info("[TVHclient] RecordLiveStream " + id);

            throw new NotImplementedException();
        }

        public async Task<LiveTvServiceStatusInfo> GetStatusInfoAsync(CancellationToken cancellationToken)
        {
            ensureConnection();

            int timeOut = await WaitForInitialLoadTask(cancellationToken);
            if (timeOut == -1 || cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[TVHclient] GetStatusInfoAsync, call canceled or timed out.");
                return new LiveTvServiceStatusInfo
                {
                    Status = LiveTvServiceStatus.Unavailable
                };
            }

            string serverName = _htsConnection.getServername();
            string serverVersion = _htsConnection.getServerversion();
            int serverProtokollVersion = _htsConnection.getServerProtocolVersion();
            string diskSpace = _htsConnection.getDiskspace();

            string serverVersionMessage = "<p>" + serverName + " " + serverVersion + "</p>"
                + "<p>HTSP protokoll version: " + serverProtokollVersion + "</p>"
                + "<p>Free diskspace: " + diskSpace + "</p>";

            //List<LiveTvTunerInfo> tvTunerInfos = await _tunerDataHelper.buildTunerInfos(cancellationToken);

            TaskWithTimeoutRunner<List<LiveTvTunerInfo>> twtr = new TaskWithTimeoutRunner<List<LiveTvTunerInfo>>(TIMEOUT);
            TaskWithTimeoutResult<List<LiveTvTunerInfo>> twtRes = await 
                twtr.RunWithTimeout(_tunerDataHelper.buildTunerInfos(cancellationToken));

            List<LiveTvTunerInfo> tvTunerInfos;
            if (twtRes.HasTimeout)
            {
                tvTunerInfos = new List<LiveTvTunerInfo>();
            }
            else
            {
                tvTunerInfos = twtRes.Result;
            }

            return new LiveTvServiceStatusInfo
            {
                Version = serverVersionMessage,
                Tuners = tvTunerInfos,
                Status = LiveTvServiceStatus.Ok,
            };
        }

        public string HomePageUrl
        {
            get { return "http://tvheadend.org/"; }
        }

        public Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ImageStream> GetChannelImageAsync(string channelId, CancellationToken cancellationToken)
        {
            // Leave as is. This is handled by supplying image url to ChannelInfo
            throw new NotImplementedException();
        }

        public Task<ImageStream> GetProgramImageAsync(string programId, string channelId, CancellationToken cancellationToken)
        {
            // Leave as is. This is handled by supplying image url to ProgramInfo
            throw new NotImplementedException();
        }

        public Task<ImageStream> GetRecordingImageAsync(string recordingId, CancellationToken cancellationToken)
        {
            // Leave as is. This is handled by supplying image url to RecordingInfo
            throw new NotImplementedException();
        }

        public Task<ChannelMediaInfo> GetChannelStream(string channelId, CancellationToken cancellationToken)
        {
            _logger.Fatal("[TVHclient] LiveTvService.GetChannelStream called for channelID '" + channelId + "'");

            throw new NotImplementedException();
        }

        public Task<ChannelMediaInfo> GetRecordingStream(string recordingId, CancellationToken cancellationToken)
        {
            _logger.Fatal("[TVHclient] LiveTvService.GetRecordingStream called for recordingId '" + recordingId + "'");

            throw new NotImplementedException();
        }

        public event EventHandler DataSourceChanged;


        private void sendDataSourceChanged()
        {
            try
            {
                EventHandler handler = DataSourceChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[TVHclient] LiveTvService.sendDataSourceChanged caught exception: " + ex.Message);
            }
        }

        public event EventHandler<RecordingStatusChangedEventArgs> RecordingStatusChanged;

        private void sendRecordingStatusChanged()
        {
            EventHandler<RecordingStatusChangedEventArgs> handler = RecordingStatusChanged;
            if (handler != null)
            {
                handler(this, new RecordingStatusChangedEventArgs());
            }
        }

        public void onMessage(HTSMessage response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        //_logger.Fatal("[TVHclient] tad add/update/delete" + response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.dvrEntryAdd(response);
                        sendRecordingStatusChanged();
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.dvrEntryUpdate(response);
                        sendRecordingStatusChanged();
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.dvrEntryDelete(response);
                        sendRecordingStatusChanged();
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.autorecEntryAdd(response);
                        sendRecordingStatusChanged();
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.autorecEntryUpdate(response);
                        sendRecordingStatusChanged();
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.autorecEntryDelete(response);
                        sendRecordingStatusChanged();
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    //case "subscriptionStart":
                    //case "subscriptionGrace":
                    //case "subscriptionStop":
                    //case "subscriptionSkip":
                    //case "subscriptionSpeed":
                    //case "subscriptionStatus":
                    //    _logger.Fatal("[TVHclient] subscription events " + response.ToString());
                    //    break;

                    //case "queueStatus":
                    //    _logger.Fatal("[TVHclient] queueStatus event " + response.ToString());
                    //    break;

                    //case "signalStatus":
                    //    _logger.Fatal("[TVHclient] signalStatus event " + response.ToString());
                    //    break;

                    //case "timeshiftStatus":
                    //    _logger.Fatal("[TVHclient] timeshiftStatus event " + response.ToString());
                    //    break;

                    //case "muxpkt": // streaming data
                    //    _logger.Fatal("[TVHclient] muxpkt event " + response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        _initialLoadFinished = true;
                        break;

                    default:
                        //_logger.Fatal("[TVHclient] Method '" + response.Method + "' not handled in LiveTvService.cs");
                        break;
                }
            }
        }

        public void onError(Exception ex)
        {
            _logger.Error("[TVHclient] HTSP error: " + ex.Message);
            _htsConnection.stop();
            _connected = false;

            sendDataSourceChanged();
        }
    }
}
