using System;
using System.Collections.Generic;
using System.Text;

namespace Naive.Console
{
    public class CommandHub
    {
        public Dictionary<string, CommandInfo> Commands = new Dictionary<string, CommandInfo>();

        public string Prompt = "CMD>";

        public CommandHub()
        {
            AddCmdHandler("help", (c) => {
                if (c.args.Length == 0) {
                    var sb = new StringBuilder(128);
                    sb.AppendLine("# Available Commands:");
                    foreach (var key in Commands.Keys) {
                        sb.Append(key).Append(" ");
                    }
                    sb.AppendLine();
                    c.Write(sb.ToString());
                } else {
                    var cmdname = c.args[0];
                    if (Commands.ContainsKey(cmdname) == false) {
                        c.WriteLine($"failed to get help: command '{cmdname}' does not exist.");
                        c.statusCode = 1;
                        return;
                    }
                    var cmdInfo = Commands[cmdname];
                    if (cmdInfo.Help == null) {
                        c.WriteLine("(no help text for this command)");
                    } else {
                        c.WriteLine(cmdInfo.Help);
                    }
                }
            }, "Usage: help [COMMAND_NAME]");
            AddCmdHandler("echo", (c) => {
                c.WriteLine(c.arg);
            });
        }

        public void AddCmdHandler(string name, CommandHandler handler)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Commands.Add(name, new CommandInfo(name, handler));
        }

        public void AddCmdHandler(string name, CommandHandler handler, string help)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Commands.Add(name, new CommandInfo(name, handler) { Help = help });
        }

        private CommandHandler getHandler(Command cmd)
        {
            try {
                return Commands[cmd.name].Handler;
            } catch (KeyNotFoundException) {
                return null;
            }
        }

        public void HandleCommand(CmdConsole con, Command cmd)
        {
            if (con == null)
                throw new ArgumentNullException(nameof(con));
            if (cmd == null)
                throw new ArgumentNullException(nameof(cmd));

            var handler = getHandler(cmd);
            if (handler != null) {
                con.RunCommand(cmd, handler);
            } else {
                cmd.statusCode = -1;
                con.Write($"Command '{cmd.name}' Not Found\n", ConsoleColor.Red);
            }
        }

        public void CmdLoop(CmdConsole con)
        {
            int lastStatusCode = 0;
            con.WriteLine("Welcome!");
            while (true) {
                string cmdline;
                if (lastStatusCode != 0) {
                    con.PromptColor32 = Color32.FromConsoleColor(ConsoleColor.Red);
                    cmdline = con.ReadLine("(" + lastStatusCode + ")" + Prompt);
                    con.PromptColor32 = Color32.FromConsoleColor(ConsoleColor.Green);
                } else {
                    cmdline = con.ReadLine(Prompt);
                }

                if (cmdline != null) {
                    if (cmdline?.Length == 0) {
                        lastStatusCode = 0;
                        continue;
                    }
                    if (cmdline == "exit") {
                        con.WriteLine("exiting command loop.");
                        break;
                    }
                    var cmd = Command.FromString(cmdline);
                    HandleCommand(con, cmd);
                    lastStatusCode = cmd.statusCode;
                    con.WriteLine("");
                } else {
                    con.WriteLine("EOF, exiting command loop.");
                    break;
                }
            }
        }
    }
}
