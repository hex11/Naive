using Naive.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.Console
{
    public class ConsoleSession
    {
        public ConsoleSession()
        {
            Console = new CmdRunner(this);
        }

        public CmdConsole Console { get; }

        public string Name { get; set; }

        private List<ConsoleClient> _bindedClients = new List<ConsoleClient>();

        private List<string> history = new List<string>();
        private int historyCharCount = 0;

        private BlockingCollection<string> inputLinesBuffer = new BlockingCollection<string>();

        public void InputLine(string text)
        {
            inputLinesBuffer.Add(text);
        }

        public void AddClient(ConsoleClient client)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            lock (_bindedClients)
                _bindedClients.Add(client);
            client.Attach(this);
            foreach (var item in history) {
                client.Write(item);
            }
        }

        public void RemoveClient(ConsoleClient client, bool throwIfFailed = false)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            lock (_bindedClients) {
                bool ok = _bindedClients.Remove(client);
                if (ok == false && throwIfFailed) {
                    throw new Exception("failed to remove client.");
                }
                if (ok) {
                    client.Attach(null);
                }
            }
        }

        public void RemoveAllClient()
        {
            foreach (var item in _bindedClients.ToArray()) {
                RemoveClient(item);
            }
        }

        private void appendHistory(string str)
        {
            lock (history) {
                history.Add(str);
                historyCharCount += str?.Length ?? 0;
                while (historyCharCount > 10000 && history.Count > 1) {
                    historyCharCount -= history[0]?.Length ?? 0;
                    history.RemoveAt(0);
                }
            }
        }

        private void foreachClient<TState>(TState state, Action<TState, ConsoleClient> action)
        {
            foreach (var item in _bindedClients) {
                action(state, item);
            }
        }

        private class CmdRunner : CmdConsole
        {
            public CmdRunner(ConsoleSession session)
            {
                _session = session;
            }

            private readonly ConsoleSession _session;

            public override string ReadLine()
            {
                var queue = _session.inputLinesBuffer;
                var line = queue.Take();
                WriteLine(line);
                return line;
            }

            public override void Write(string text)
            {
                _session.appendHistory(text);
                _session.foreachClient(text, (txt, r) => {
                    r.Write(txt);
                });
            }

            public override void WriteLine(string text)
            {
                _session.appendHistory(text);
                _session.appendHistory("\r\n");
                _session.foreachClient(text, (txt, r) => {
                    r.WriteLine(txt);
                });
            }
        }

        public override string ToString()
        {
            return Name ?? base.ToString();
        }
    }
}