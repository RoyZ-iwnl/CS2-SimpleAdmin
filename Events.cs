﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using Dapper;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text;

namespace CS2_SimpleAdmin;

public partial class CS2_SimpleAdmin
{
	private void registerEvents()
	{
		//RegisterListener<OnClientAuthorized>(OnClientAuthorized);
		//RegisterListener<OnClientConnect>(OnClientConnect);
		RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
		//RegisterListener<OnClientDisconnect>(OnClientDisconnect);
		//RegisterEventHandler<EventPlayerConnectFull>(OnPlayerFullConnect);
		RegisterListener<Listeners.OnMapStart>(OnMapStart);
		RegisterListener<Listeners.OnClientVoice>(OnClientVoice);
		//RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
		//RegisterEventHandler<EventRoundStart>(OnRoundStart);
		AddCommandListener("say", OnCommandSay);
		AddCommandListener("say_team", OnCommandTeamSay);
	}

	private void OnClientVoice(int playerSlot)
	{
		PlayerPenaltyManager playerPenaltyManager = new PlayerPenaltyManager();
		if (!playerPenaltyManager.IsSlotInPenalties(playerSlot)) return;

		CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
		if (player is null || !player.IsValid) return;

		if (playerPenaltyManager.IsPenalized(playerSlot, PenaltyType.Mute) || playerPenaltyManager.IsPenalized(playerSlot, PenaltyType.Silence))
			player.VoiceFlags = VoiceFlags.Muted;
	}

	[GameEventHandler]
	private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
	{
#if DEBUG
		Logger.LogCritical("[OnRoundEnd]");
#endif
		godPlayers.Clear();
		return HookResult.Continue;
	}

	private HookResult OnCommandSay(CCSPlayerController? player, CommandInfo info)
	{
		if (player is null || !player.IsValid || player.IsBot || player.IsHLTV || info.GetArg(1).Length == 0) return HookResult.Continue;

		PlayerPenaltyManager playerPenaltyManager = new PlayerPenaltyManager();

		if (playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag) || playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence))
			return HookResult.Handled;

		return HookResult.Continue;
	}

	private HookResult OnCommandTeamSay(CCSPlayerController? player, CommandInfo info)
	{
		if (player is null || !player.IsValid || player.IsBot || player.IsHLTV || info.GetArg(1).Length == 0) return HookResult.Continue;

		PlayerPenaltyManager playerPenaltyManager = new PlayerPenaltyManager();

		if (playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag) || playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence))
			return HookResult.Handled;

		if (info.GetArg(1).StartsWith("@"))
		{
			StringBuilder sb = new();

			if (AdminManager.PlayerHasPermissions(player, "@css/chat"))
			{
				sb.Append(_localizer!["sa_adminchat_template_admin", player!.PlayerName, info.GetArg(1).Remove(0, 1)]);
				foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV && AdminManager.PlayerHasPermissions(p, "@css/chat")))
				{
					p.PrintToChat(sb.ToString());
				}
			}
			else
			{
				sb.Append(_localizer!["sa_adminchat_template_player", player!.PlayerName, info.GetArg(1).Remove(0, 1)]);
				player.PrintToChat(sb.ToString());
				foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV && AdminManager.PlayerHasPermissions(p, "@css/chat")))
				{
					p.PrintToChat(sb.ToString());
				}
			}

			return HookResult.Handled;
		}

		return HookResult.Continue;
	}

	public void OnClientPutInServer(int playerSlot)
	{
		CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);
#if DEBUG
		Logger.LogCritical("[OnPlayerConnect] Before check");

#endif
		if (player is null || !player.IsValid || player.IsBot || player.IsHLTV) return;
#if DEBUG
		Logger.LogCritical("[OnPlayerConnect] After Check");
#endif
		string? ipAddress = !string.IsNullOrEmpty(player.IpAddress) ? player.IpAddress.Split(":")[0] : null;

		if (
			ipAddress != null && bannedPlayers.Contains(ipAddress) ||
			bannedPlayers.Contains(player.SteamID.ToString())
			)
		{
			Helper.KickPlayer((ushort)player.UserId!, "Banned");
			return;
		}

		if (_database == null)
			return;

		PlayerInfo playerInfo = new PlayerInfo
		{
			UserId = player.UserId,
			Index = (ushort)player.Index,
			Slot = player.Slot,
			SteamId = player.SteamID.ToString(),
			Name = player.PlayerName,
			IpAddress = ipAddress
		};

		BanManager _banManager = new(_database, Config);
		MuteManager _muteManager = new(_database);

		Task.Run(async () =>
		{
			if (await _banManager.IsPlayerBanned(playerInfo))
			{
				if (playerInfo.IpAddress != null && !bannedPlayers.Contains(playerInfo.IpAddress))
					bannedPlayers.Add(playerInfo.IpAddress);

				if (playerInfo.SteamId != null && !bannedPlayers.Contains(playerInfo.SteamId))
					bannedPlayers.Add(playerInfo.SteamId);

				Server.NextFrame(() =>
				{
					if (playerInfo.UserId != null)
						Helper.KickPlayer((ushort)playerInfo.UserId, "Banned");
				});

				return;
			}

			List<dynamic> activeMutes = await _muteManager.IsPlayerMuted(playerInfo.SteamId);

			if (activeMutes.Count > 0)
			{
				PlayerPenaltyManager playerPenaltyManager = new PlayerPenaltyManager();
				foreach (var mute in activeMutes)
				{
					string muteType = mute.type;

					if (muteType == "GAG")
					{
						/*
						if (playerInfo.Slot.HasValue && !gaggedPlayers.Contains(playerInfo.Slot.Value))
							gaggedPlayers.Add(playerInfo.Slot.Value);
						*/
						playerPenaltyManager.AddPenalty(1, PenaltyType.Gag, DateTime.Parse(mute.ends), mute.duration);

						if (TagsDetected)
						{
							Server.NextFrame(() =>
							{
								Server.ExecuteCommand($"css_tag_mute {playerInfo.SteamId}");
							});
						}
					}
					else if (muteType == "MUTE")
					{
						playerPenaltyManager.AddPenalty(1, PenaltyType.Mute, DateTime.Parse(mute.ends), mute.duration);

					}
					else
					{
						/*
						 * if (playerInfo.Slot.HasValue && !gaggedPlayers.Contains(playerInfo.Slot.Value))
							gaggedPlayers.Add(playerInfo.Slot.Value);
						*/
						playerPenaltyManager.AddPenalty(1, PenaltyType.Silence, DateTime.Parse(mute.ends), mute.duration);

						Server.NextFrame(() =>
						{
							if (player.IsValid)
							{
								if (TagsDetected)
								{
									Server.ExecuteCommand($"css_tag_mute {playerInfo.SteamId}");
								}
							}
						});
					}
				}
			}
		});

		return;
	}

	[GameEventHandler]
	public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
	{
		if (@event.Userid is null || !@event.Userid.IsValid)
			return HookResult.Continue;

		CCSPlayerController? player = @event.Userid;

#if DEBUG
		Logger.LogCritical("[OnClientDisconnect] Before");
#endif

		if (player.IsBot || player.IsHLTV) return HookResult.Continue;

#if DEBUG
		Logger.LogCritical("[OnClientDisconnect] After Check");
#endif

		PlayerPenaltyManager playerPenaltyManager = new PlayerPenaltyManager();
		playerPenaltyManager.RemoveAllPenalties(player.Slot);
		//RemoveFromConcurrentBag(gaggedPlayers, player.Slot);
		RemoveFromConcurrentBag(silentPlayers, player.Slot);
		RemoveFromConcurrentBag(godPlayers, player.Slot);

		if (player.AuthorizedSteamID != null && AdminSQLManager._adminCache.TryGetValue(player.AuthorizedSteamID, out DateTime? expirationTime)
			&& expirationTime <= DateTime.Now)
		{
			AdminManager.ClearPlayerPermissions(player.AuthorizedSteamID);
			AdminManager.RemovePlayerAdminData(player.AuthorizedSteamID);
		}

		if (TagsDetected)
			Server.ExecuteCommand($"css_tag_unmute {player.SteamID}");

		return HookResult.Continue;
	}

	private void OnMapStart(string mapName)
	{
		Random random = new Random();
		//gaggedPlayers.Clear();
		godPlayers.Clear();
		silentPlayers.Clear();

		PlayerPenaltyManager playerPenaltyManager = new PlayerPenaltyManager();
		playerPenaltyManager.RemoveAllPenalties();

		_database = new(dbConnectionString);

		if (_database == null) return;

		AddTimer(random.Next(60, 80), async () =>
		{
#if DEBUG
			Logger.LogCritical("[OnMapStart] Expired check");
#endif
			AdminSQLManager _adminManager = new(_database);
			BanManager _banManager = new(_database, Config);
			MuteManager _muteManager = new(_database);
			await _banManager.ExpireOldBans();
			await _muteManager.ExpireOldMutes();
			await _adminManager.DeleteOldAdmins();

			bannedPlayers.Clear();

			Server.NextFrame(() =>
			{
				foreach (CCSPlayerController player in Helper.GetValidPlayers())
				{
					if (playerPenaltyManager.IsSlotInPenalties(player.Slot))
					{
						if (!playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Mute) && !playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence))
							player.VoiceFlags = VoiceFlags.Normal;

						if (!playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag) && !playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence))
						{
							if (TagsDetected)
								Server.ExecuteCommand($"css_tag_unmute {player!.SteamID}");
						}

						if (
							!playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence) &&
							!playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Mute) &&
							!playerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag)
						)
						{
							player.VoiceFlags = VoiceFlags.Normal;

							if (TagsDetected)
								Server.ExecuteCommand($"css_tag_unmute {player!.SteamID}");
						}
					}
				}
			});

			playerPenaltyManager.RemoveExpiredPenalties();

		}, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT | CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);

		string? path = Path.GetDirectoryName(ModuleDirectory);
		if (Directory.Exists(path + "/CS2-Tags"))
		{
			TagsDetected = true;
		}

		AddTimer(2.0f, async () =>
		{
			string? address = $"{ConVar.Find("ip")!.StringValue}:{ConVar.Find("hostport")!.GetPrimitiveValue<int>()}";
			string? hostname = ConVar.Find("hostname")!.StringValue;

			await Task.Run(async () =>
			{
				AdminSQLManager _adminManager = new(_database);
				try
				{
					await using (var connection = await _database.GetConnectionAsync())
					{
						bool addressExists = await connection.ExecuteScalarAsync<bool>(
							"SELECT COUNT(*) FROM sa_servers WHERE address = @address",
							new { address });

						if (!addressExists)
						{
							await connection.ExecuteAsync(
								"INSERT INTO sa_servers (address, hostname) VALUES (@address, @hostname)",
								new { address, hostname });
						}
						else
						{
							await connection.ExecuteAsync(
								"UPDATE `sa_servers` SET hostname = @hostname WHERE address = @address",
								new { address, hostname });
						}

						int? serverId = await connection.ExecuteScalarAsync<int>(
							"SELECT `id` FROM `sa_servers` WHERE `address` = @address",
							new { address });

						ServerId = serverId;
					}
				}
				catch (Exception)
				{
					if (_logger != null)
						_logger.LogCritical("Unable to create or get server_id");
				}
				await _adminManager.GiveAllFlags();
			});
		}, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);


		AddTimer(2.0f, () =>
		{
			ConVar? botQuota = ConVar.Find("bot_quota");

			if (botQuota != null && botQuota.GetPrimitiveValue<int>() > 0)
			{
				Logger.LogInformation("Due to bugs with bots (game bug), consider disabling bots by setting `bot_quota 0` in the gamemode config if your server crashes after a map change.");
				Logger.LogWarning("Due to bugs with bots (game bug), consider disabling bots by setting `bot_quota 0` in the gamemode config if your server crashes after a map change.");
				Logger.LogCritical("Due to bugs with bots (game bug), consider disabling bots by setting `bot_quota 0` in the gamemode config if your server crashes after a map change.");
			}
		});
	}

	[GameEventHandler]
	private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
	{
		CCSPlayerController? player = @event.Userid;

		if (player is null || @event.Attacker == null || !player.IsValid || !player.PlayerPawn.IsValid || player.PlayerPawn.Value == null
			|| player.IsBot || player.IsHLTV || player.PlayerPawn.IsValid || player.Connected == PlayerConnectedState.PlayerDisconnecting
			|| @event.Attacker.Connected == PlayerConnectedState.PlayerDisconnecting)
			return HookResult.Continue;

		if (godPlayers.Contains(player.Slot) && player.PawnIsAlive)
		{
			player.Health = 100;
			player.PlayerPawn.Value.Health = 100;
		}

		return HookResult.Continue;
	}

}