using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Naive.Console
{
    public class ConsoleHub
    {
        public List<ConsoleSession> Sessions { get; } = new List<ConsoleSession>();
        public CommandHub CommandHub { get; set; } = new CommandHub();

        public void AddConsole(ConsoleSession console)
        {
            if (console == null)
                throw new ArgumentNullException(nameof(console));
            if (Sessions.Contains(console))
                throw new Exception("this console has been already added.");

            Sessions.Add(console);
        }

        public void RemoveConsole(ConsoleSession console)
        {
            if (console == null)
                throw new ArgumentNullException(nameof(console));

            Sessions.Remove(console);
        }

        public ConsoleSession CreateCmdSession()
        {
            var session = new ConsoleSession();
            StartSafeThread(() => {
                this.AddConsole(session);
                this.CommandHub.CmdLoop(session.Console);
                this.RemoveConsole(session);
                session.RemoveAllClient();
            }, "CmdSession");
            return session;
        }

        public void StartSessionSelectLoop(ConsoleClient client)
        {
            var are = new AutoResetEvent(false);
            void check(ConsoleClient c)
            {
                if (c.Session == null) {
                    are.Set();
                }
            }
            try {
                client.SessionChanged += check;
                bool isFirst = true;
                while (!client.Closed) {
                    _sessionSelect(client, isFirst);
                    isFirst = false;
                    are.WaitOne();
                }
            } finally {
                client.SessionChanged -= check;
                are.Dispose();
            }
        }

        private void _sessionSelect(ConsoleClient client, bool autoCreate)
        {
            if (Sessions.Count == 0 && autoCreate) {
                client.Attach(CreateCmdSession());
                return;
            }
            var tempsession = new ConsoleSession();
            void checkDetach(ConsoleClient cli)
            {
                if (cli.Session != tempsession) {
                    cli.SessionChanged -= checkDetach;
                    tempsession.InputLine(null);
                }
            }
            client.SessionChanged += checkDetach;
            client.Attach(tempsession);
            var c = tempsession.Console;
            while (true) {
                c.WriteLine("Select Session:");
                var sessions = Sessions.ToArray();
                for (int i = 0; i < sessions.Length; i++) {
                    c.WriteLine($"{i} {sessions[i]}");
                }
                c.WriteLine("n New Session");
                var line = c.ReadLine("> ");
                if (line == null)
                    return;
                if (int.TryParse(line, out int n) && n >= 0 && n < sessions.Length) {
                    client.Attach(sessions[n]);
                    break;
                } else if (line == "n") {
                    client.Attach(CreateCmdSession());
                    break;
                }
            }
        }


        private static Thread StartSafeThread(ThreadStart start, string name, Action<Exception> onException = null)
        {
            var thr = new Thread(GetSafeThreadStart(start, onException, name)) { Name = name };
            thr.Start();
            return thr;
        }

        private static ThreadStart GetSafeThreadStart(ThreadStart start, Action<Exception> onException, string name)
        {
            return () => {
                try {
                    start();
                } catch (Exception e) {
                    if (onException != null) {
                        try {
                            onException(e);
                        } catch (Exception e2) {
                            // ignore
                        }
                    }
                }
            };
        }
    }
}
