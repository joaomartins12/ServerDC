using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util.Commands;

namespace GameServer.Util
{
    public class GameChatCommands : ChatCommands
    {
        public GameChatCommands()
        {
            // As per legal requirements, this shall not be removed or changed!
            Add("copyright", "/copyright", 0x0, "Gets the copyright", CopyrightCommandHandler);
            Add("about", "/about", 0x0, "Gets the copyright", CopyrightCommandHandler);
            // As per legal requirements, this shall not be removed or changed!

            Add("help", "/help [Command]", 0x1000, "Shows help about a command", HelpCommandHandler);

            Add("notice", "/notice [Message]", 0x1000, "Send a GM notice", NoticeCommandHandler);
            Add("weather", "/weather [fine/cloudy/foggy/rain/sunset]", 0x8000, "Changes the current weather",
                WeatherCommandHandler);
            Add("kick", "/kick [Character Name]", 0x1000, "Kicks the user", KickCommandHandler);
            Add("ban", "/ban [Character Name]", 0x8000, "Bans the user forever", BanCommandHandler);
            Add("money", "/money [Character Name] [Amount]", 0x8000, "Gives the charactername money",
                MoneyCommandHandler);
            Add("exp", "/exp [Character Name] [Amount]", 0x8000, "Gives the user experience", ExpCommandHandler);

            Add("mute", "/mute [Character Name]", 0x8000, "Mutes/Unmutes the character from chat", MuteCommandHandler);
            Add("tempmute", "/mute [Character Name]", 0x8000, "Mutes/Unmutes the character from chat", MuteCommandHandler);

            Add("gm", "/gm", 0x1000, "Toggles your GM Status", ToggleGmStatusCommandHandler);
            Add("perfprobe", "/perfprobe int [1-19] [value] | /perfprobe float [1-19] [value] | /perfprobe off", 0x8000,
                "Temporarily probes raw StatUpdate fields as int or IEEE-754 float", PerformanceProbeCommandHandler);
            Add("perfresearch", "/perfresearch", 0x8000,
                "Scans imported client TDF tables for vehicle-performance candidates", PerformanceResearchCommandHandler);
        }

        private static CommandResult PerformanceResearchCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            var character = sender.User == null ? null : sender.User.ActiveCharacter;
            if (character == null)
            {
                sender.SendChatMessage("Performance research failed: no active character.");
                return CommandResult.Fail;
            }

            var activeCar = character.ActiveCar;
            if (activeCar == null && character.GarageVehicles != null)
                activeCar = character.GarageVehicles.Find(v => v != null && v.CarId == character.ActiveVehicleId);
            if (activeCar == null)
            {
                sender.SendChatMessage("Performance research failed: no active vehicle.");
                return CommandResult.Fail;
            }

            var stats = VehicleStatResolver.Resolve(activeCar);
            if (stats == null)
            {
                sender.SendChatMessage("Performance research failed: vehicle stats could not be resolved.");
                return CommandResult.Fail;
            }

            var equipped = EquippedItemStatResolver.Resolve(character, activeCar);
            sender.SendChatMessage("Vehicle performance research started in background. This scans the imported client_* tables.");

            Task.Run(delegate
            {
                try
                {
                    var result = VehiclePerformanceCandidateExporter.Export(character, activeCar, stats, equipped);
                    try
                    {
                        sender.SendChatMessage("Performance research complete: " + result.CandidateRows +
                                               " candidates from " + result.TablesScanned + " tables / " +
                                               result.RowsScanned + " rows. Check Logs\\...\\GameServer\\Research.");
                    }
                    catch
                    {
                        // The player may have disconnected while the background scan was running.
                    }
                }
                catch (System.Exception ex)
                {
                    QuietLog.Write("VehiclePerformanceResearch", "Candidate scan failed: {0}", ex);
                    try { sender.SendChatMessage("Performance research failed: " + ex.Message); }
                    catch { }
                }
            });

            return CommandResult.Okay;
        }

        private static CommandResult PerformanceProbeCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 1)
                return CommandResult.InvalidArgument;

            if (string.Equals(args[0], "off", System.StringComparison.OrdinalIgnoreCase))
            {
                VehiclePerformanceProbe.Disable();
                QuietLog.Write("VehiclePerformanceProbe", "Probe disabled by {0}", sender.User == null ? "UNKNOWN" : sender.User.Username);
                sender.SendChatMessage("Vehicle performance probe disabled. Reopen the inventory/stat panel to refresh.");
                return CommandResult.Okay;
            }

            // Backwards-compatible syntax: /perfprobe 11 300
            if (!string.Equals(args[0], "int", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[0], "float", System.StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count < 2)
                    return CommandResult.InvalidArgument;

                int legacyField;
                int legacyValue;
                if (!int.TryParse(args[0], out legacyField) || legacyField < 1 || legacyField > 19)
                    return CommandResult.InvalidArgument;
                if (!int.TryParse(args[1], out legacyValue))
                    return CommandResult.InvalidArgument;

                VehiclePerformanceProbe.ConfigureInt(legacyField, legacyValue);
                QuietLog.Write("VehiclePerformanceProbe", "Configured field={0} mode=Int value={1} by {2}",
                    legacyField, legacyValue, sender.User == null ? "UNKNOWN" : sender.User.Username);
                sender.SendChatMessage("Performance probe INT field " + legacyField + " = " + legacyValue + ". Reopen the inventory/stat panel.");
                return CommandResult.Okay;
            }

            if (args.Count < 3)
                return CommandResult.InvalidArgument;

            int field;
            if (!int.TryParse(args[1], out field) || field < 1 || field > 19)
                return CommandResult.InvalidArgument;

            if (string.Equals(args[0], "int", System.StringComparison.OrdinalIgnoreCase))
            {
                int value;
                if (!int.TryParse(args[2], out value))
                    return CommandResult.InvalidArgument;

                VehiclePerformanceProbe.ConfigureInt(field, value);
                QuietLog.Write("VehiclePerformanceProbe", "Configured field={0} mode=Int rawInt={1} by {2}",
                    field, value, sender.User == null ? "UNKNOWN" : sender.User.Username);
                sender.SendChatMessage("Performance probe INT field " + field + " = " + value + ". Reopen the inventory/stat panel.");
                return CommandResult.Okay;
            }

            float floatValue;
            if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out floatValue) &&
                !float.TryParse(args[2], NumberStyles.Float, CultureInfo.CurrentCulture, out floatValue))
                return CommandResult.InvalidArgument;

            VehiclePerformanceProbe.ConfigureFloat(field, floatValue);
            var raw = VehiclePerformanceProbe.RawValue;
            QuietLog.Write("VehiclePerformanceProbe", "Configured field={0} mode=Float float={1} rawInt={2} rawHex=0x{3:X8} by {4}",
                field,
                floatValue.ToString("R", CultureInfo.InvariantCulture),
                raw,
                unchecked((uint)raw),
                sender.User == null ? "UNKNOWN" : sender.User.Username);
            sender.SendChatMessage("Performance probe FLOAT field " + field + " = " +
                                   floatValue.ToString("R", CultureInfo.InvariantCulture) +
                                   " (raw 0x" + unchecked((uint)raw).ToString("X8") + "). Reopen the inventory/stat panel.");
            return CommandResult.Okay;
        }

        private static CommandResult MuteCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 1)
                return CommandResult.InvalidArgument;

            var characterName = args[0];

            var client = GameServer.Instance.Server.GetClient(characterName);
            if (client?.User == null) return CommandResult.Fail;
            if(client.User.Status == UserStatus.Banned) return CommandResult.Fail;

            client.User.Status = client.User.Status == UserStatus.Muted ? UserStatus.Normal : UserStatus.Muted;
            if(command == "mute")
                AccountModel.Update(GameServer.Instance.Database.Connection, client.User);

            var newStatusStr = client.User.Status == UserStatus.Muted ? "muted" : "unmuted";
            sender.SendChatMessage($"User {client.User.Username} was {newStatusStr}");

            if (sender.User.GmFlag)
                sender.SendChatMessage($"You were {newStatusStr} by {sender.User.Username}");
            else
                sender.SendChatMessage($"You were {newStatusStr} by a GM");

            return CommandResult.Okay;
        }

        private static CommandResult ToggleGmStatusCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            sender.User.GmFlag = !sender.User.GmFlag;
            var status = "invisible";
            if (sender.User.GmFlag)
                status = "visible";

            sender.SendChatMessage($"Your GM Status is now: {status}");
            return CommandResult.Okay;
        }

        private static CommandResult HelpCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 1)
                return CommandResult.InvalidArgument;
            var cmd = args[0];
            var helpCmd = GameServer.ChatCommands.GetCommand(cmd);
            if(helpCmd == null)
                return CommandResult.Fail;

            if ((UserPermission)helpCmd.RequiredPermission > sender.User.Permission)
                return CommandResult.Fail;

            sender.SendChatMessage(helpCmd.Usage + " - " + helpCmd.Description);

            return CommandResult.Okay;
        }

        private static CommandResult CopyrightCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            sender.SendChatMessage($"Drift City Neo City v{Shared.Util.Version.GetVersion()} Copyright 2016 GigaToni");
            return CommandResult.Okay;
        }

        private static CommandResult NoticeCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count == 0)
                return CommandResult.InvalidArgument;

            var msg = string.Join(" ", args);
            var ack = new ChatMessageAnswer()
            {
                MessageType = "channel",
                SenderCharacterName = "GM",
                Message = msg,
            }.CreatePacket();

            server.Broadcast(ack);
            return CommandResult.Okay;
        }

        private static CommandResult WeatherCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 1)
                return CommandResult.InvalidArgument;

            var ack = new Packet(Packets.WeatherAck);
            switch (args[0])
            {
                case "fine": ack.Writer.Write(0); break;
                case "cloudy": ack.Writer.Write(1); break;
                case "foggy": ack.Writer.Write(2); break;
                case "rain": ack.Writer.Write(3); break;
                case "sunset": ack.Writer.Write(4); break;
                default: return CommandResult.InvalidArgument;
            }

            GameServer.Instance.Server.Broadcast(ack);
            return CommandResult.Okay;
        }

        private static CommandResult KickCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 1)
                return CommandResult.InvalidArgument;

            var characterName = args[0];
            var client = GameServer.Instance.Server.GetClient(characterName);
            if (client?.User == null) return CommandResult.Fail;

            client.KillConnection($"Kicked by {sender.User.Username}");
            sender.SendChatMessage($"User {characterName} ({client.User.Username}) kicked!");
            return CommandResult.Okay;
        }

        private static CommandResult MoneyCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 2)
                return CommandResult.InvalidArgument;

            var characterName = args[0];
            long amount;
            if (!long.TryParse(args[1], out amount)) return CommandResult.InvalidArgument;
            if (amount <= 0) return CommandResult.InvalidArgument;

            var client = GameServer.Instance.Server.GetClient(characterName);
            if (client?.User.ActiveCharacter == null) return CommandResult.Fail;

            client.User.ActiveCharacter.MitoMoney += amount;
            CharacterModel.Update(GameServer.Instance.Database.Connection, client.User.ActiveCharacter);

            sender.SendChatMessage($"{amount} Mito given to {characterName} ({client.User.Username})");

            client.Send(new CharUpdateAnswer()
            {
                Character = client.User.ActiveCharacter
            }.CreatePacket());

            return CommandResult.Okay;
        }

        private static CommandResult ExpCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 2)
                return CommandResult.InvalidArgument;

            var characterName = args[0];
            int amount;
            if (!int.TryParse(args[1], out amount)) return CommandResult.InvalidArgument;
            if (amount <= 0) return CommandResult.InvalidArgument;

            var client = GameServer.Instance.Server.GetClient(characterName);
            if (client?.User.ActiveCharacter == null) return CommandResult.Fail;

            bool levelUp;
            bool useBonus = false;
            bool useBonus500Mita = false;
            client.User.ActiveCharacter.CalculateExp(amount, out levelUp, useBonus, useBonus500Mita);
            CharacterModel.Update(GameServer.Instance.Database.Connection, client.User.ActiveCharacter);

            sender.SendChatMessage($"{amount} EXP given to {characterName} ({client.User.Username})");

            client.Send(new CharUpdateAnswer()
            {
                Character = client.User.ActiveCharacter
            }.CreatePacket());

            return CommandResult.Okay;
        }

        private static CommandResult BanCommandHandler(DefaultServer server, Client sender, string command,
            IList<string> args)
        {
            if (args.Count < 1)
                return CommandResult.InvalidArgument;

            var characterName = args[0];
            var client = GameServer.Instance.Server.GetClient(characterName);
            if (client?.User == null) return CommandResult.Fail;

            client.User.Status = UserStatus.Banned;
            AccountModel.Update(GameServer.Instance.Database.Connection, client.User);

            client.KillConnection($"Banned by {sender.User.Username}");
            sender.SendChatMessage($"User {characterName} ({client.User.Username}) banned!");

            return CommandResult.Okay;
        }
    }
}
