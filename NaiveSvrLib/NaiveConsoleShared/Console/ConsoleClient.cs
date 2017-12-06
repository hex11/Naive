using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Naive.Console
{
    public abstract class ConsoleClient
    {
        public ConsoleSession Session { get; private set; }

        public event Action<ConsoleClient> SessionChanged;

        public bool Closed { get; set; } = false;

        public virtual void Attach(ConsoleSession session)
        {
            if (session == Session)
                return;
            var oldSession = Session;
            Session = null;
            oldSession?.RemoveClient(this);
            this.Session = session;
            Session?.AddClient(this);
            SessionChanged?.Invoke(this);
        }

        public virtual void Detach()
        {
            Attach(null);
        }

        public void Close()
        {
            Closed = true;
            Detach();
        }

        public void InputLine(string text)
        {
            Session?.InputLine(text);
            if (Session == null) {
                WriteLine("(no session)");
            }
        }

        public abstract void Write(string text);
        public virtual void WriteLine(string text)
        {
            Write(text + Environment.NewLine);
        }
    }

    public class LambdaConsoleClient : ConsoleClient
    {
        public Action<string> OnWrite;
        public override void Write(string text)
        {
            OnWrite?.Invoke(text);
        }
    }
}
