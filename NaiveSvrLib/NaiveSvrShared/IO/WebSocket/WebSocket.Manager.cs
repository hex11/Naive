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
    }
}
