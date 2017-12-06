using System;
using System.Collections.Generic;

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
                    c.WriteLine("# Available Commands:");
                    foreach (var key in Commands.Keys) {
                        c.Write(key + " ");
                    }
                    c.WriteLine("");
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
                cmd.statusCode = int.MinValue;
                con.WriteLine($"Command '{cmd.name}' Not Found");
            }
        }

        public void CmdLoop(CmdConsole con)
        {
            con.WriteLine("Welcome!");
            while (true) {
                var cmd = con.ReadLine(Prompt);
                if (cmd != null) {
                    if (cmd == "")
                        continue;
                    if (cmd == "exit") {
                        con.WriteLine("exiting command loop.");
                        break;
                    }
                    HandleCommand(con, Command.FromString(cmd));
                    con.WriteLine("");
                } else {
                    con.WriteLine("EOF, exiting command loop.");
                    break;
                }
            }
        }
    }
}
