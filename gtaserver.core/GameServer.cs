﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GTAServer.ProtocolMessages;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace GTAServer
{
    public class GameServer
    {
        public string Location => Directory.GetCurrentDirectory();
        public NetPeerConfiguration Config;

        public List<Client> Clients { get; set; }
        public int MaxPlayers { get; set; }
        public int Port { get; set; }
        public string GamemodeName { get; set; } // This is only what is sent to the client. No GM loading is done yet.
        public string Name { get; set; }
        public string Password { get; set; }
        public bool PasswordProtected => !string.IsNullOrEmpty(Password);
        public string MasterServer { get; set; }
        public string BackupMasterServer { get; set; }
        public bool AnnounceSelf { get; set; }
        public bool AllowNicknames { get; set; }
        public bool AllowOutdatedClients { get; set; }
        public readonly ScriptVersion ServerVersion = ScriptVersion.VERSION_0_9_3;
        public string LastKickedIP { get; set; }
        public Client LastKickedClient { get; set; }


        private DateTime _lastAnnounceDateTime;
        private NetServer _server;
        private ILogger logger;

        public GameServer(int port, string name, string gamemodeName)
        {
            var loggerFactory = new LoggerFactory()
                .AddConsole()
                .AddDebug();
            logger = loggerFactory.CreateLogger<GameServer>();
            logger.LogInformation("Server ready to start");
            Clients = new List<Client>();
            MaxPlayers = 32;
            GamemodeName = gamemodeName;
            Name = name;
            Port = port;
            MasterServer = "https://gtamaster.nofla.me";
            BackupMasterServer = "http://fakemaster.nofla.me";
            Config = new NetPeerConfiguration("GTAVOnlineRaces") { Port = port };
            Config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            Config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
            Config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
            Config.EnableMessageType(NetIncomingMessageType.ConnectionLatencyUpdated);
            _server = new NetServer(Config);

            logger.LogInformation($"NetServer created with port {Config.Port}");

        }

        public void Start()
        {
            logger.LogInformation("Server starting");
            _server.Start();
            if (AnnounceSelf)
            {
                AnnounceToMaster();
            }
            // TODO: Gamemode loading here... we need a module/plugin system first
        }

        private void AnnounceToMaster()
        {
#if !DEBUG
            logger.LogDebug("Announcing to master server");
            _lastAnnounceDateTime = DateTime.Now;
            logger.LogDebug("Server announcer not implemented");
#endif
            // TODO: implement server announcing
        }

        public void Tick()
        {
            if (AnnounceSelf && DateTime.Now.Subtract(_lastAnnounceDateTime).TotalMinutes >= 5)
            {
                AnnounceToMaster();
            }
            //throw new Exception("test");
            NetIncomingMessage msg;
            while ((msg = _server.ReadMessage()) != null)
            {
                Client client = null;
                lock (Clients)
                {
                    try
                    {
                        client = Clients.Where(d => d.NetConnection != null)
                            .Where(d => d.NetConnection.RemoteUniqueIdentifier != 0)
                            .Where(d => msg.SenderConnection != null) // almost pointless but w/e
                            .First(
                                d =>
                                    d.NetConnection.RemoteUniqueIdentifier ==
                                    msg.SenderConnection.RemoteUniqueIdentifier);
                    }
                    catch (InvalidOperationException e)
                    {
                        // ignored because we make a new client below if there is none, which is when an InvalidOperationException is thrown
                    }
                }
                if (client == null) client = new Client(msg.SenderConnection);
                //logger.LogInformation("Packet received - type: " + ((NetIncomingMessageType)msg.MessageType).ToString());
                switch (msg.MessageType)
                {
                    case NetIncomingMessageType.UnconnectedData:
                        var ucType = msg.ReadString();
                        // ReSharper disable once ConvertIfStatementToSwitchStatement
                        if (ucType == "ping")
                        {
                            logger.LogInformation("Ping received from " + msg.SenderEndPoint.Address.ToString());
                            var reply = _server.CreateMessage("pong");
                            _server.SendMessage(reply, client.NetConnection, NetDeliveryMethod.ReliableOrdered);
                        }
                        else if (ucType == "query")
                        {
                            var playersOnline = 0;
                            lock (Clients) playersOnline = Clients.Count;
                            logger.LogInformation("Query received from " + msg.SenderEndPoint.Address.ToString());
                            var reply = _server.CreateMessage($"{Name}%{PasswordProtected}%{playersOnline}%{MaxPlayers}%{GamemodeName}");
                            _server.SendMessage(reply, client.NetConnection, NetDeliveryMethod.ReliableOrdered);
                        }
                        break;
                    case NetIncomingMessageType.VerboseDebugMessage:
                    case NetIncomingMessageType.DebugMessage:
                        logger.LogDebug("Network (Verbose)DebugMessage: " + msg.ReadString());
                        break;
                    case NetIncomingMessageType.WarningMessage:
                        logger.LogWarning("Network WarningMessage: " + msg.ReadString());
                        break;
                    case NetIncomingMessageType.ErrorMessage:
                        logger.LogError("Network ErrorMessage: " + msg.ReadString());
                        break;
                    case NetIncomingMessageType.ConnectionLatencyUpdated:
                        client.Latency = msg.ReadFloat();
                        break;
                    case NetIncomingMessageType.ConnectionApproval:
                        HandleClientConnectionApproval(client, msg);
                        break;
                    case NetIncomingMessageType.StatusChanged:
                        HandleClientStatusChange(client, msg);
                        break;
                    case NetIncomingMessageType.DiscoveryRequest:
                        HandleClientDiscoveryRequest(client, msg);
                        break;
                    case NetIncomingMessageType.Data:
                        HandleClientIncomingData(client, msg);
                        break;
                    default:
                        // We shouldn't get packets reaching this, so throw warnings when it happens.
                        logger.LogWarning("Unknown packet received: " +
                                          ((NetIncomingMessageType)msg.MessageType).ToString());
                        break;

                }
                _server.Recycle(msg);
            }
        }
        private void HandleClientConnectionApproval(Client client, NetIncomingMessage msg)
        {
            var type = msg.ReadInt32();
            var length = msg.ReadInt32();
            var connReq = Util.DeserializeBinary<ConnectionRequest>(msg.ReadBytes(length));
            if (connReq == null)
            {
                DenyConnect(client, "Connection is null, this is most likely a bug in the client.", true, msg);
                return;
            }

            // If nicknames are disabled on the server, set the nickname to the player's social club name.
            if (!AllowNicknames)
            {
                SendNotificationToPlayer(client,
                    $"Nicknames are disabled on this server. Your nickname has been set to {connReq.Name}");
                connReq.DisplayName = connReq.Name;
            }


            logger.LogInformation(
                $"New connection request: {connReq.DisplayName}@{msg.SenderEndPoint.Address.ToString()} | Game version: {connReq.GameVersion.ToString()} | Script version: {connReq.ScriptVersion.ToString()}");

            var latestScriptVersion = Enum.GetValues(typeof(ScriptVersion)).Cast<ScriptVersion>().Last();
            if (!AllowOutdatedClients &&
                (ScriptVersion)connReq.ScriptVersion != latestScriptVersion)
            {
                var latestReadableScriptVersion = latestScriptVersion.ToString();
                latestReadableScriptVersion = Regex.Replace(latestReadableScriptVersion, "VERSION_", "",
                    RegexOptions.IgnoreCase);
                latestReadableScriptVersion = Regex.Replace(latestReadableScriptVersion, "_", ".",
                    RegexOptions.IgnoreCase);

                logger.LogInformation($"Client {client.DisplayName} tried to connect with an outdated script version {connReq.ScriptVersion.ToString()} but the server requires {latestScriptVersion.ToString()}");
                DenyConnect(client, $"Please update to version ${latestReadableScriptVersion} from http://bit.ly/gtacoop", true, msg);
                return;
            }
            else if ((ScriptVersion)connReq.ScriptVersion != latestScriptVersion)
            {
                SendNotificationToPlayer(client, "You are currently on an outdated client. Please go to http://bit.ly/gtacoop and update.");
            }
            else if ((ScriptVersion)connReq.ScriptVersion == ScriptVersion.VERSION_UNKNOWN)
            {
                logger.LogInformation($"Client {client.DisplayName} tried to connect with an unknown script version (client too old?)");
                DenyConnect(client, $"Unknown version. Please re-download GTACoop from http://bit.ly/gtacoop", true, msg);
                return;
            }
            var numClients = 0;
            lock (Clients) numClients = Clients.Count;
            if (numClients >= MaxPlayers)
            {
                logger.LogInformation($"Player tried to join while server is full: {client.DisplayName}");
                DenyConnect(client, "No available player slots.", true, msg);
            }

            if (PasswordProtected && connReq.Password != Password)
            {
                logger.LogInformation($"Client {client.DisplayName} tried to connect with the wrong password.");
                DenyConnect(client, "Wrong password.", true, msg);
            }

            lock (Clients) if (Clients.Any(c => c.DisplayName == connReq.DisplayName))
                {
                    DenyConnect(client, "A player already exists with the current display name.");
                }

            client.ApplyConnectionRequest(connReq);

            var channelHail = _server.CreateMessage();
            channelHail.Write(GetChannelForClient(client));
            client.NetConnection.Approve(channelHail);
        }
        private void HandleClientStatusChange(Client client, NetIncomingMessage msg)
        {
            var newStatus = (NetConnectionStatus)msg.ReadByte();
            switch (newStatus)
            {
                case NetConnectionStatus.Connected:
                    logger.LogInformation($"Connected: {client.DisplayName}@{msg.SenderEndPoint.Address.ToString()}");
                    SendNotificationToAll($"Player connected: {client.DisplayName}");
                    break;

                case NetConnectionStatus.Disconnected:
                    lock (Clients)
                    {
                        if (Clients.Contains(client))
                        {
                            if (!client.Silent)
                            {
                                if (client.Kicked)
                                {
                                    if (string.IsNullOrEmpty(client.KickReason)) client.KickReason = "Unknown";
                                    SendNotificationToAll(
                                        $"Player kicked: {client.DisplayName} - Reason: {client.KickReason}");
                                }
                                else
                                {
                                    SendNotificationToAll(
                                        $"Player disconnected: {client.DisplayName}");
                                }
                            }
                            var dcMsg = new PlayerDisconnect()
                            {
                                Id = client.NetConnection.RemoteUniqueIdentifier
                            };

                            SendToAll(dcMsg, PacketType.PlayerDisconnect, true);

                            if (client.Kicked)
                            {
                                logger.LogInformation(
                                    $"Player kicked: {client.DisplayName}@{msg.SenderEndPoint.Address.ToString()}");
                                LastKickedClient = client;
                                LastKickedIP = client.NetConnection.RemoteEndPoint.ToString();
                            }
                            else
                            {
                                logger.LogInformation($"Player disconnected: {client.DisplayName}@{msg.SenderEndPoint.Address.ToString()}");
                            }
                            Clients.Remove(client);
                        }
                        break;
                    }
                // resharper was bugging me about not having the below case statements
                case NetConnectionStatus.None:
                case NetConnectionStatus.InitiatedConnect:
                case NetConnectionStatus.ReceivedInitiation:
                case NetConnectionStatus.RespondedAwaitingApproval:
                case NetConnectionStatus.RespondedConnect:
                case NetConnectionStatus.Disconnecting:
                default:
                    break;
            }
        }
        private void HandleClientDiscoveryRequest(Client client, NetIncomingMessage msg)
        {
            var responsePkt = _server.CreateMessage();
            var discoveryResponse = new DiscoveryResponse
            {
                ServerName = Name,
                MaxPlayers = MaxPlayers,
                PasswordProtected = PasswordProtected,
                Gamemode = GamemodeName,
                Port = Port,
            };
            lock (Clients) discoveryResponse.PlayerCount = Clients.Count;

            var serializedResponse = Util.SerializeBinary(discoveryResponse);
            responsePkt.Write((int)PacketType.DiscoveryResponse);
            responsePkt.Write(serializedResponse.Length);
            responsePkt.Write(serializedResponse);
            logger.LogInformation($"Server status requested by {msg.SenderEndPoint.Address.ToString()}");
            _server.SendDiscoveryResponse(responsePkt, msg.SenderEndPoint);
        }

        private void HandleClientIncomingData(Client client, NetIncomingMessage msg)
        {
            var packetType = (PacketType)msg.ReadInt32();

            switch (packetType)
            {
                case PacketType.ChatData:
                    {
                        // TODO: This code really could use refactoring.. right now only trying to make sure this all works on .NET Core and fixing small issues.
                        var len = msg.ReadInt32();
                        var chatData = Util.DeserializeBinary<ChatData>(msg.ReadBytes(len));
                        if (chatData != null)
                        {
                            var chatMsg = new ChatMessage(chatData, client);

                            if (!chatMsg.Suppress)
                            {
                                chatData.Id = client.NetConnection.RemoteUniqueIdentifier;
                                chatData.Sender = "";
                                if (!string.IsNullOrWhiteSpace(chatMsg.Prefix))
                                    chatData.Sender += "[" + chatMsg.Prefix + "] ";
                                chatData.Sender += chatMsg.Sender.DisplayName;

                                if (!string.IsNullOrWhiteSpace(chatMsg.Suffix))
                                    chatData.Sender += $" ({chatMsg.Suffix}) ";
                                SendToAll(chatData, PacketType.ChatData, true);
                                logger.LogInformation($"[Chat] <{chatData.Sender}>: {chatData.Message}");
                            }
                        }
                    }
                    break;
                case PacketType.VehiclePositionData:
                    {
                        var len = msg.ReadInt32();
                        var vehicleData = Util.DeserializeBinary<VehicleData>(msg.ReadBytes(len));
                        if (vehicleData != null)
                        {
                            vehicleData.Id = client.NetConnection.RemoteUniqueIdentifier;
                            vehicleData.Name = client.Name;
                            vehicleData.Latency = client.Latency;

                            client.Health = vehicleData.PlayerHealth;
                            client.LastKnownPosition = vehicleData.Position;
                            client.IsInVehicle = false;

                            SendToAll(vehicleData, PacketType.VehiclePositionData, false, client);
                        }
                    }
                    break;
                case PacketType.PedPositionData:
                    {
                        var len = msg.ReadInt32();
                        var pedPosData = Util.DeserializeBinary<PedData>(msg.ReadBytes(len));
                        if (pedPosData != null)
                        {
                            pedPosData.Id = client.NetConnection.RemoteUniqueIdentifier;
                            pedPosData.Name = client.DisplayName;
                            pedPosData.Latency = client.Latency;

                            client.Health = pedPosData.PlayerHealth;
                            client.LastKnownPosition = pedPosData.Position;
                            client.IsInVehicle = false;

                            SendToAll(pedPosData, PacketType.PedPositionData, false, client);
                        }
                    }
                    break;
                case PacketType.NpcVehPositionData:
                    {
                        var len = msg.ReadInt32();
                        var vehData = Util.DeserializeBinary<VehicleData>(msg.ReadBytes(len));
                        if (vehData != null)
                        {
                            vehData.Id = client.NetConnection.RemoteUniqueIdentifier;
                            SendToAll(vehData, PacketType.NpcVehPositionData, false, client);
                        }
                    }
                    break;
                case PacketType.NpcPedPositionData:
                    {
                        var len = msg.ReadInt32();
                        var pedData = Util.DeserializeBinary<PedData>(msg.ReadBytes(len));
                        if (pedData != null)
                        {
                            pedData.Id = msg.SenderConnection.RemoteUniqueIdentifier;
                        }
                        SendToAll(pedData, PacketType.NpcPedPositionData, false, client);
                    }
                    break;
                case PacketType.WorldSharingStop:
                    {
                        var dcObj = new PlayerDisconnect()
                        {
                            Id = client.NetConnection.RemoteUniqueIdentifier
                        };
                        SendToAll(dcObj, PacketType.WorldSharingStop, true);
                    }
                    break;
                case PacketType.NativeResponse:
                    {
                        var len = msg.ReadInt32();
                        var nativeResponse = Util.DeserializeBinary<NativeResponse>(msg.ReadBytes(len));
                        if (nativeResponse == null) return; // TODO: check if there is a callback.
                        object response = nativeResponse.Response;
                        if (response is IntArgument)
                        {
                            response = ((IntArgument)response).Data;
                        }
                        else if (response is UIntArgument)
                        {
                            response = ((UIntArgument)response).Data;
                        }
                        else if (response is StringArgument)
                        {
                            response = ((StringArgument)response).Data;
                        }
                        else if (response is FloatArgument)
                        {
                            response = ((FloatArgument)response).Data;
                        }
                        else if (response is BooleanArgument)
                        {
                            response = ((BooleanArgument)response).Data;
                        }
                        else if (response is Vector3Argument)
                        {
                            var tmp = (Vector3Argument)response;
                            response = new Vector3()
                            {
                                X = tmp.X,
                                Y = tmp.Y,
                                Z = tmp.Z
                            };
                        }
                        // TODO: call the callback (if there is one) and remove it
                    }
                    break;
                case PacketType.PlayerSpawned:
                    {
                        logger.LogInformation("Player spawned: " + client.DisplayName);
                    }
                    break;

                case PacketType.PlayerDisconnect:
                    break;
                case PacketType.DiscoveryResponse:
                    break;
                case PacketType.ConnectionRequest:
                    break;
                case PacketType.NativeCall:
                    break;
                case PacketType.NativeTick:
                    break;
                case PacketType.NativeTickRecall:
                    break;
                case PacketType.NativeOnDisconnect:
                    break;
                case PacketType.NativeOnDisconnectRecall:
                    break;
                default:
                    // ReSharper disable once NotResolvedInText
                    // resharper wants to see a variable name in the below... w/e.
                    throw new ArgumentOutOfRangeException("Received unknown packet type. Server out of date or modded client?");
            }
        }

        public void SendToAll(object dataToSend, PacketType packetType, bool packetIsImportant)
        {
            var data = Util.SerializeBinary(dataToSend);
            var msg = _server.CreateMessage();
            msg.Write((int)packetType);
            msg.Write(data.Length);
            msg.Write(data);
            _server.SendToAll(msg, packetIsImportant ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.ReliableSequenced);
        }

        public void SendToAll(object dataToSend, PacketType packetType, bool packetIsImportant, Client clientToExclude)
        {
            var data = Util.SerializeBinary(dataToSend);
            var msg = _server.CreateMessage();
            msg.Write((int)packetType);
            msg.Write(data.Length);
            msg.Write(data);
            _server.SendToAll(msg, clientToExclude.NetConnection, packetIsImportant ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.ReliableSequenced, GetChannelForClient(clientToExclude));
        }

        public void DenyConnect(Client player, string reason, bool silent = true, NetIncomingMessage msg = null,
            int duraction = 60)
        {
            player.NetConnection.Deny(reason);
            logger.LogInformation($"Player rejected from server: {player.DisplayName} for {reason}");
            if (!silent)
            {
                SendNotificationToAll($"Player rejected by server: {player.DisplayName} - {reason}");
            }

            Clients.Remove(player);
            if (msg != null) _server.Recycle(msg);
        }

        public int GetChannelForClient(Client c)
        {
            lock (Clients) return (Clients.IndexOf(c) % 31) + 1;
        }

        // Native call functions
        public List<NativeArgument> ParseNativeArguments(params object[] args) // literally copypasted from old gtaserver
        {
            var list = new List<NativeArgument>();
            foreach (var o in args)
            {
                if (o is int)
                {
                    list.Add(new IntArgument() { Data = ((int)o) });
                }
                else if (o is uint)
                {
                    list.Add(new UIntArgument() { Data = ((uint)o) });
                }
                else if (o is string)
                {
                    list.Add(new StringArgument() { Data = ((string)o) });
                }
                else if (o is float)
                {
                    list.Add(new FloatArgument() { Data = ((float)o) });
                }
                else if (o is bool)
                {
                    list.Add(new BooleanArgument() { Data = ((bool)o) });
                }
                else if (o is Vector3)
                {
                    var tmp = (Vector3)o;
                    list.Add(new Vector3Argument()
                    {
                        X = tmp.X,
                        Y = tmp.Y,
                        Z = tmp.Z,
                    });
                }
                else if (o is LocalPlayerArgument)
                {
                    list.Add((LocalPlayerArgument)o);
                }
                else if (o is LocalGamePlayerArgument)
                {
                    list.Add((LocalGamePlayerArgument)o);
                }
            }

            return list;
        }

        public void SendNativeCallToPlayer(Client player, ulong hash, params object[] arguments)
        {
            var obj = new NativeData
            {
                Hash = hash,
                Arguments = ParseNativeArguments(arguments)
            };

            var bin = Util.SerializeBinary(obj);

            var msg = _server.CreateMessage();

            msg.Write((int)PacketType.NativeCall);
            msg.Write(bin.Length);
            msg.Write(bin);

            player.NetConnection.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, GetChannelForClient(player));
        }

        public void SendNativeCallToAll(ulong hash, params object[] arguments)
        {
            var obj = new NativeData
            {
                Hash = hash,
                Arguments = ParseNativeArguments(arguments),
                ReturnType = null,
                Id = null,
            };

            var bin = Util.SerializeBinary(obj);

            var msg = _server.CreateMessage();

            msg.Write((int)PacketType.NativeCall);
            msg.Write(bin.Length);
            msg.Write(bin);

            _server.SendToAll(msg, NetDeliveryMethod.ReliableOrdered);
        }

        // Notification stuff
        public void SendNotificationToPlayer(Client player, string message, bool flashing = false)
        {
            for (var i = 0; i < message.Length; i += 99)
            {
                SendNativeCallToPlayer(player, 0x202709F4C58A0424, "STRING");
                SendNativeCallToPlayer(player, 0x6C188BE134E074AA, message.Substring(i, Math.Min(99, message.Length - i)));
                SendNativeCallToPlayer(player, 0xF020C96915705B3A, flashing, true);
            }
        }

        public void SendNotificationToAll(string message, bool flashing = false)
        {
            for (var i = 0; i < message.Length; i += 99)
            {
                SendNativeCallToAll(0x202709F4C58A0424, "STRING");
                SendNativeCallToAll(0x6C188BE134E074AA, message.Substring(i, Math.Min(99, message.Length - i)));
                SendNativeCallToAll(0xF020C96915705B3A, flashing, true);
            }
        }

    }
}
