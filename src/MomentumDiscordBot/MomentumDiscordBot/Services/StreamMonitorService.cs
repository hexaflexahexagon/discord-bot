﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using MomentumDiscordBot.Models;
using MomentumDiscordBot.Utilities;

namespace MomentumDiscordBot.Services
{
    /// <summary>
    ///     Service to provide a list of current streamers playing Momentum Mod.
    /// </summary>
    public class StreamMonitorService
    {
        private readonly Dictionary<string, ulong> _cachedStreamsIds;
        private readonly Config _config;
        private readonly DiscordSocketClient _discordClient;
        private readonly SocketTextChannel _textChannel;
        private readonly TwitchApiService _twitchApiService;
        private Timer _intervalFunctionTimer;
        private List<string> _streamSoftBanList = new List<string>();

        public StreamMonitorService(DiscordSocketClient discordClient, TimeSpan updateInterval, ulong channelId,
            Config config)
        {
            _config = config;
            _discordClient = discordClient;
            _twitchApiService = new TwitchApiService();
            _textChannel = _discordClient.GetChannel(channelId) as SocketTextChannel;

            _cachedStreamsIds = new Dictionary<string, ulong>();

            _intervalFunctionTimer = new Timer(UpdateCurrentStreamersAsync, null, TimeSpan.Zero, updateInterval);
            DeleteAllChannelEmbedsAsync().GetAwaiter().GetResult();
        }

        private async void UpdateCurrentStreamersAsync(object state)
        {
            var streams = await _twitchApiService.GetLiveMomentumModStreamersAsync();
            var streamIds = streams.Select(x => x.Id);

            // If the cached stream id's isn't in the fetched stream id, it is an ended stream
            var endedStreams = _cachedStreamsIds.Where(x => !streamIds.Contains(x.Key));
            foreach (var (endedStreamId, messageId) in endedStreams)
            {
                // If the stream was soft banned, remove it
                if (_streamSoftBanList.Contains(endedStreamId))
                {
                    _streamSoftBanList.Remove(endedStreamId);
                }

                await _textChannel.DeleteMessageAsync(messageId);
                _cachedStreamsIds.Remove(endedStreamId);
            }

            // Filter out soft banned streams
            var filteredStreams = streams.Where(x => !_streamSoftBanList.Contains(x.Id));

            foreach (var stream in filteredStreams)
            {
                var embed = new EmbedBuilder
                {
                    Title = stream.Title,
                    Color = Color.Purple,
                    Author = new EmbedAuthorBuilder
                    {
                        Name = stream.UserName,
                        IconUrl = await _twitchApiService.GetStreamerIconUrlAsync(stream.UserId),
                        Url = $"https://twitch.tv/{stream.UserName}"
                    },
                    ImageUrl = stream.ThumbnailUrl.Replace("{width}", "1280").Replace("{height}", "720"),
                    Description = stream.ViewerCount + " viewers",
                    Url = $"https://twitch.tv/{stream.UserName}"
                }.Build();

                // New streams are not in the cache
                if (!_cachedStreamsIds.ContainsKey(stream.Id))
                {
                    // New stream, send a new message
                    var message =
                        await _textChannel.SendMessageAsync(MentionUtils.MentionRole(_config.LivestreamMentionRoleId),
                            embed: embed);

                    _cachedStreamsIds.Add(stream.Id, message.Id);
                }
                else
                {
                    // Existing stream, update message with new information
                    if (_cachedStreamsIds.TryGetValue(stream.Id, out var messageId))
                    {
                        var oldMessage = await _textChannel.GetMessageAsync(messageId);
                        if (oldMessage is RestUserMessage oldRestMessage)
                        {
                            await oldRestMessage.ModifyAsync(x => x.Embed = embed);
                        }
                    }
                }
            }

            // Check for soft-banned stream, when a mod deletes the message
            var existingSelfMessages = (await _textChannel.GetMessagesAsync(limit: 200).FlattenAsync()).FromSelf(_discordClient);
            var softBannedMessages = _cachedStreamsIds.Where(x => existingSelfMessages.All(y => y.Id != x.Value));
            _streamSoftBanList.AddRange(softBannedMessages.Select(x => x.Key));
        }

        private async Task DeleteAllChannelEmbedsAsync()
        {
            try
            {
                // Delete current messages
                var messages = await _textChannel.GetMessagesAsync().FlattenAsync();

                // Delete existing bot messages simultaneously
                var deleteTasks = messages.FromSelf(_discordClient)
                    .Select(async x => await x.DeleteAsync());
                await Task.WhenAll(deleteTasks);
            }
            catch
            {
                // Could have old messages, safe to ignore
            }
        }
    }
}