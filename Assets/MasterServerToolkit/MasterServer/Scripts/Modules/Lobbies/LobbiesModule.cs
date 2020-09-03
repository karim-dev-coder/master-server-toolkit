﻿using MasterServerToolkit.Logging;
using MasterServerToolkit.Networking;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MasterServerToolkit.MasterServer
{
    public class LobbiesModule : BaseServerModule, IGamesProvider
    {
        #region INSPECTOR

        [Header("Configuration")]
        public int createLobbiesPermissionLevel = 0;
        [Tooltip("If true, don't allow player to create a lobby if he has already joined one")]
        public bool dontAllowCreatingIfJoined = true;
        [Tooltip("How many lobbies can a user join concurrently")]
        public int joinedLobbiesLimit = 1;

        #endregion

        private int nextLobbyId;
        protected Dictionary<string, ILobbyFactory> factories;
        protected Dictionary<int, ILobby> lobbies;

        public SpawnersModule SpawnersModule { get; protected set; }
        public RoomsModule RoomsModule { get; protected set; }

        protected override void Awake()
        {
            base.Awake();

            AddOptionalDependency<SpawnersModule>();
            AddOptionalDependency<RoomsModule>();
        }

        public override void Initialize(IServer server)
        {
            // Get dependencies
            SpawnersModule = server.GetModule<SpawnersModule>();
            RoomsModule = server.GetModule<RoomsModule>();

            factories = factories ?? new Dictionary<string, ILobbyFactory>();
            lobbies = lobbies ?? new Dictionary<int, ILobby>();

            server.SetHandler((short)MstMessageCodes.CreateLobby, CreateLobbyRequestHandle);
            server.SetHandler((short)MstMessageCodes.JoinLobby, JoinLobbyRequestHandler);
            server.SetHandler((short)MstMessageCodes.LeaveLobby, LeaveLobbyRequestHandler);
            server.SetHandler((short)MstMessageCodes.SetLobbyProperties, HandleSetLobbyProperties);
            server.SetHandler((short)MstMessageCodes.SetMyLobbyProperties, HandleSetMyProperties);
            server.SetHandler((short)MstMessageCodes.JoinLobbyTeam, HandleJoinTeam);
            server.SetHandler((short)MstMessageCodes.LobbySendChatMessage, HandleSendChatMessage);
            server.SetHandler((short)MstMessageCodes.LobbySetReady, HandleSetReadyStatus);
            server.SetHandler((short)MstMessageCodes.LobbyStartGame, HandleStartGame);
            server.SetHandler((short)MstMessageCodes.GetLobbyRoomAccess, HandleGetLobbyRoomAccess);

            server.SetHandler((short)MstMessageCodes.GetLobbyMemberData, HandleGetLobbyMemberData);
            server.SetHandler((short)MstMessageCodes.GetLobbyInfo, HandleGetLobbyInfo);
        }

        protected virtual bool CheckIfHasPermissionToCreate(IPeer peer)
        {
            var extension = peer.GetExtension<SecurityInfoPeerExtension>();
            return extension.PermissionLevel >= createLobbiesPermissionLevel;
        }

        /// <summary>
        /// Add new lobby factory to list
        /// </summary>
        /// <param name="factory"></param>
        public void AddFactory(ILobbyFactory factory)
        {
            // In case the module has not been initialized yet
            if (factories == null)
            {
                factories = new Dictionary<string, ILobbyFactory>();
            }

            if (factories.ContainsKey(factory.Id))
            {
                logger.Warn("You are overriding a factory with same id");
            }

            factories[factory.Id] = factory;
        }

        /// <summary>
        /// Adds new lobby to list of lobbies
        /// </summary>
        /// <param name="lobby"></param>
        /// <returns></returns>
        public bool AddLobby(ILobby lobby)
        {
            if (lobbies.ContainsKey(lobby.Id))
            {
                logger.Error("Failed to add a lobby - lobby with same id already exists");
                return false;
            }

            lobbies.Add(lobby.Id, lobby);

            lobby.OnDestroyedEvent += OnLobbyDestroyedEventHandler;

            return true;
        }

        /// <summary>
        /// Invoked, when lobby is destroyed
        /// </summary>
        /// <param name="lobby"></param>
        protected virtual void OnLobbyDestroyedEventHandler(ILobby lobby)
        {
            lobbies.Remove(lobby.Id);
            lobby.OnDestroyedEvent -= OnLobbyDestroyedEventHandler;
        }


        /// <summary>
        /// Get or create lobby extension for the peer
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        protected virtual LobbyUserPeerExtension GetOrCreateLobbyUserPeerExtension(IPeer peer)
        {
            var extension = peer.GetExtension<LobbyUserPeerExtension>();

            if (extension == null)
            {
                extension = new LobbyUserPeerExtension(peer);
                peer.AddExtension(extension);
            }

            return extension;
        }

        /// <summary>
        /// Create new unique lobby Id
        /// </summary>
        /// <returns></returns>
        public int GenerateLobbyId()
        {
            return nextLobbyId++;
        }

        #region INCOMING MESSAGES HANDLERS

        protected virtual void CreateLobbyRequestHandle(IIncommingMessage message)
        {
            // We may need to check permission of requester
            if (!CheckIfHasPermissionToCreate(message.Peer))
            {
                message.Respond("Insufficient permissions", ResponseStatus.Unauthorized);
                return;
            }

            // Let's get or create new lobby user peer extension
            var lobbyUser = GetOrCreateLobbyUserPeerExtension(message.Peer);

            // If peer is already in a lobby and system does not allow to create if user is joined
            if (dontAllowCreatingIfJoined && lobbyUser.CurrentLobby != null)
            {
                message.Respond("You are already in a lobby", ResponseStatus.Failed);
                return;
            }

            // Deserialize properties of the lobby
            var options = MstProperties.FromBytes(message.AsBytes());

            // Try get factory ID
            if (!options.Has(MstDictKeys.lobbyFactoryId))
            {
                message.Respond("Invalid request (undefined factory)", ResponseStatus.Failed);
                return;
            }

            // Get the lobby factory
            factories.TryGetValue(options.AsString(MstDictKeys.lobbyFactoryId), out ILobbyFactory factory);

            if (factory == null)
            {
                message.Respond("Unavailable lobby factory", ResponseStatus.Failed);
                return;
            }

            var newLobby = factory.CreateLobby(options, message.Peer);

            if (!AddLobby(newLobby))
            {
                message.Respond("Lobby registration failed", ResponseStatus.Error);
                return;
            }

            logger.Info("Lobby created: " + newLobby.Id);

            // Respond with success and lobby id
            message.Respond(newLobby.Id, ResponseStatus.Success);
        }

        /// <summary>
        /// Handles a request from user to join a lobby
        /// </summary>
        /// <param name="message"></param>
        protected virtual void JoinLobbyRequestHandler(IIncommingMessage message)
        {
            var lobbyUser = GetOrCreateLobbyUserPeerExtension(message.Peer);

            if (lobbyUser.CurrentLobby != null)
            {
                message.Respond("You're already in a lobby", ResponseStatus.Failed);
                return;
            }

            var lobbyId = message.AsInt();

            lobbies.TryGetValue(lobbyId, out ILobby lobby);

            if (lobby == null)
            {
                message.Respond("Lobby was not found", ResponseStatus.Failed);
                return;
            }

            if (!lobby.AddPlayer(lobbyUser, out string error))
            {
                message.Respond(error ?? "Failed to add player to lobby", ResponseStatus.Failed);
                return;
            }

            var data = lobby.GenerateLobbyData(lobbyUser);

            message.Respond(data, ResponseStatus.Success);
        }

        /// <summary>
        /// Handles a request from user to leave a lobby
        /// </summary>
        /// <param name="message"></param>
        protected virtual void LeaveLobbyRequestHandler(IIncommingMessage message)
        {
            var lobbyId = message.AsInt();

            lobbies.TryGetValue(lobbyId, out ILobby lobby);

            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);

            if (lobby != null)
            {
                lobby.RemovePlayer(lobbiesExt);
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleSetLobbyProperties(IIncommingMessage message)
        {
            var data = message.Deserialize(new LobbyPropertiesSetPacket());

            lobbies.TryGetValue(data.LobbyId, out ILobby lobby);

            if (lobby == null)
            {
                message.Respond("Lobby was not found", ResponseStatus.Failed);
                return;
            }

            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);

            foreach (var dataProperty in data.Properties.ToDictionary())
            {
                if (!lobby.SetProperty(lobbiesExt, dataProperty.Key, dataProperty.Value))
                {
                    message.Respond("Failed to set the property: " + dataProperty.Key,
                        ResponseStatus.Failed);
                    return;
                }
            }

            message.Respond(ResponseStatus.Success);
        }

        private void HandleSetMyProperties(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);

            var lobby = lobbiesExt.CurrentLobby;

            if (lobby == null)
            {
                message.Respond("Lobby was not found", ResponseStatus.Failed);
                return;
            }

            var properties = new Dictionary<string, string>().FromBytes(message.AsBytes());

            var player = lobby.GetMemberByExtension(lobbiesExt);

            foreach (var dataProperty in properties)
            {
                // We don't change properties directly,
                // because we want to allow an implementation of lobby
                // to do "sanity" checking
                if (!lobby.SetPlayerProperty(player, dataProperty.Key, dataProperty.Value))
                {
                    message.Respond("Failed to set property: " + dataProperty.Key, ResponseStatus.Failed);
                    return;
                }
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleSetReadyStatus(IIncommingMessage message)
        {
            var isReady = message.AsInt() > 0;

            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            if (lobby == null)
            {
                message.Respond("You're not in a lobby", ResponseStatus.Failed);
                return;
            }

            var member = lobby.GetMemberByExtension(lobbiesExt);

            if (member == null)
            {
                message.Respond("Invalid request", ResponseStatus.Failed);
                return;
            }

            lobby.SetReadyState(member, isReady);
            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleJoinTeam(IIncommingMessage message)
        {
            var data = message.Deserialize(new LobbyJoinTeamPacket());

            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            if (lobby == null)
            {
                message.Respond("You're not in a lobby", ResponseStatus.Failed);
                return;
            }

            var player = lobby.GetMemberByExtension(lobbiesExt);

            if (player == null)
            {
                message.Respond("Invalid request", ResponseStatus.Failed);
                return;
            }

            if (!lobby.TryJoinTeam(data.TeamName, player))
            {
                message.Respond("Failed to join a team: " + data.TeamName, ResponseStatus.Failed);
                return;
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleSendChatMessage(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            var member = lobby.GetMemberByExtension(lobbiesExt);

            // Invalid request
            if (member == null)
            {
                return;
            }

            lobby.ChatMessageHandler(member, message);
        }

        protected virtual void HandleStartGame(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            if (!lobby.StartGameManually(lobbiesExt))
            {
                message.Respond("Failed starting the game", ResponseStatus.Failed);
                return;
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleGetLobbyRoomAccess(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbyUserPeerExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            lobby.GameAccessRequestHandler(message);
        }

        protected virtual void HandleGetLobbyMemberData(IIncommingMessage message)
        {
            var data = message.Deserialize(new IntPairPacket());
            var lobbyId = data.A;
            var peerId = data.B;

            lobbies.TryGetValue(lobbyId, out ILobby lobby);

            if (lobby == null)
            {
                message.Respond("Lobby not found", ResponseStatus.Failed);
                return;
            }

            var member = lobby.GetMemberByPeerId(peerId);

            if (member == null)
            {
                message.Respond("Player is not in the lobby", ResponseStatus.Failed);
                return;
            }

            message.Respond(member.GenerateDataPacket(), ResponseStatus.Success);
        }

        protected virtual void HandleGetLobbyInfo(IIncommingMessage message)
        {
            var lobbyId = message.AsInt();

            lobbies.TryGetValue(lobbyId, out ILobby lobby);

            if (lobby == null)
            {
                message.Respond("Lobby not found", ResponseStatus.Failed);
                return;
            }

            message.Respond(lobby.GenerateLobbyData(), ResponseStatus.Success);
        }

        #endregion

        public IEnumerable<GameInfoPacket> GetPublicGames(IPeer peer, MstProperties filters)
        {
            return lobbies.Values.Select(lobby => new GameInfoPacket()
            {
                Address = lobby.GameIp + ":" + lobby.GamePort,
                Id = lobby.Id,
                IsPasswordProtected = false,
                MaxPlayers = lobby.MaxPlayers,
                Name = lobby.Name,
                OnlinePlayers = lobby.PlayerCount,
                CustomOptions = GetPublicLobbyProperties(peer, lobby, filters),
                Type = GameInfoType.Lobby
            });
        }

        public virtual MstProperties GetPublicLobbyProperties(IPeer peer, ILobby lobby, MstProperties playerFilters)
        {
            return lobby.GetPublicProperties(peer);
        }
    }
}