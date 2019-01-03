using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Naive.HttpSvr
{
    public partial class WebSocket
    {
        static class Manager
        {
            static readonly object _manageLock = new object();

            static Thread _manageThread;

            static readonly AutoResetEvent _manageIntervalShrinked = new AutoResetEvent(false);

            private static void ManageThread(object obj)
            {
                while (true) {
                    var interval = _manageInterval;
                    if (_manageIntervalShrinked.WaitOne(interval)) {
                        Thread.Sleep(_manageInterval);
                    }

                    _RoughTime = CalcCurrentTime();
                    _dontCalcTime = true;

                    try {
                        CheckManagedWebsocket();
                        RunAdditionalTasks();
                    } finally {
                        _dontCalcTime = false;
                    }

                    if (ManagedWebSockets.Count == 0 && AdditionalManagementTasks.Count == 0) {
                        lock (_manageLock) {
                            if (ManagedWebSockets.Count == 0 && AdditionalManagementTasks.Count == 0) {
                                _manageThread = null;
                                Logging.debug("websocket management thread stopped.");
                                break;
                            }
                        }
                    }
                }
            }

            public static void CheckManageTask()
            {
                var interval = _manageInterval;
                if (_manageThread == null)
                    lock (_manageLock)
                        if (_manageThread == null) {
                            Logging.debug("websocket management thread started.");
                            _manageThread = new Thread(ManageThread);
                            _manageThread.Start();
                        }
            }

            private static void RunAdditionalTasks()
            {
                for (int i = AdditionalManagementTasks.Count - 1; i >= 0; i--) {
                    Func<bool> item;
                    try {
                        item = AdditionalManagementTasks[i];
                    } catch (Exception) {
                        continue; // ignore
                    }
                    bool remove = true;
                    try {
                        remove = item();
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "management additional task " + item);
                        remove = true;
                    }
                    if (remove)
                        lock (AdditionalManagementTasks)
                            AdditionalManagementTasks.RemoveAt(i);
                }
            }

            private static void CheckManagedWebsocket()
            {
                for (int i = ManagedWebSockets.Count - 1; i >= 0; i--) {
                    WebSocket item;
                    try {
                        item = ManagedWebSockets[i];
                    } catch (Exception) {
                        continue; // ignore
                    }
                    try {
                        var delta = CurrentTime - item.LatestActiveTime;
                        var closeTimeout = item.ManagedCloseTimeout;
                        var pingTimeout = item.ManagedPingTimeout;
                        if (closeTimeout <= 0)
                            continue;
                        if (pingTimeout <= 0)
                            pingTimeout = closeTimeout;
                        if (delta > closeTimeout
                            && (item._manageState == ManageState.PingSent || item.ConnectionState != States.Open)) {
                            Logging.warning($"{item} timed out, closing.");
                            item._manageState = ManageState.TimedoutClosed;
                            item.Close();
                        } else if (pingTimeout > 0 && delta > pingTimeout && item.ConnectionState == States.Open) {
                            if (item._manageState == ManageState.Normal) {
                                Logging.debug($"{item} pinging.");
                                item._manageState = ManageState.PingSent;
                                item.BeginSendPing();
                            } else {
                                Logging.debug($"{item} still pinging.");
                            }
                        } else {
                            //item._manageState = ManageState.Normal;
                        }
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "WebSocket manage task exception, ignored.");
                    }
                }
            }
        }

        public static List<WebSocket> ManagedWebSockets = new List<WebSocket>();

        static List<Func<bool>> AdditionalManagementTasks = new List<Func<bool>>();

        public static void AddManagementTask(Func<bool> func)
        {
            lock (AdditionalManagementTasks)
                AdditionalManagementTasks.Add(func);
            Manager.CheckManageTask();
        }

        private static int _timeAcc = 1;
        public static int TimeAcc
        {
            get { return _timeAcc; }
            set {
                ConfigManageTask(value, _manageInterval);
            }
        }

        private static int _manageInterval = 3000;
        public static int ManageInterval
        {
            get { return _manageInterval; }
            set {
                ConfigManageTask(_timeAcc, value);
            }
        }

        public static void ConfigManageTask(int timeAcc, int manageInterval)
        {
            if (timeAcc <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeAcc));
            if (manageInterval <= 0)
                throw new ArgumentOutOfRangeException(nameof(manageInterval));
            _timeAcc = timeAcc;
            _manageInterval = manageInterval;

            Manager.CheckManageTask();
        }

        private static int _RoughTime = 0;

        private static bool _dontCalcTime = false;

        public static int CurrentTime
        {
            get {
                if (_dontCalcTime) {
                    return _RoughTime;
                } else {
                    var calcTime = CalcCurrentTime();
                    _RoughTime = calcTime;
                    return calcTime;
                }
            }
        }

        public static int CurrentTimeRough => _RoughTime;

        static long _totalMsUntilLastTicks = 0;

        static int _lastTicks = Environment.TickCount;

        static SpinLock _lastTicksLock = new SpinLock(false);

        private static int CalcCurrentTime()
        {
            var curTicks = Environment.TickCount;
            var laTicks = _lastTicks;

            if (curTicks >= laTicks) {
                var delta = curTicks - laTicks;
                return (int)((_totalMsUntilLastTicks + delta) / 1000);
            } else {
                bool lt = false;
                _lastTicksLock.Enter(ref lt);
                var delta = (int.MaxValue - laTicks) + (curTicks - int.MinValue) + 1;
                _lastTicks = curTicks;
                _totalMsUntilLastTicks += delta;
                var ret = (int)(_totalMsUntilLastTicks / 1000);
                _lastTicksLock.Exit(false);
                return ret;
            }
        }

        public static int TotalPingsSent;
        public static int TotalPongsReceived;
        public static int TotalPingsReceived;
    }
}
