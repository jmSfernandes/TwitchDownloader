using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore
{
    public sealed class ChatDownloader
    {
        private readonly ChatDownloadOptions downloadOptions;

        private static HttpClient httpClient = new HttpClient()
        {
            BaseAddress = new Uri("https://gql.twitch.tv/gql"),
            DefaultRequestHeaders = {{"Client-ID", "kd1unb4b3q4t58fwlpcbzcbnm76a8fp"}}
        };

        private static readonly Regex _bitsRegex = new(
            @"(?<=(?:\s|^)(?:4Head|Anon|Bi(?:bleThumb|tBoss)|bday|C(?:h(?:eer|arity)|orgo)|cheerwal|D(?:ansGame|oodleCheer)|EleGiggle|F(?:rankerZ|ailFish)|Goal|H(?:eyGuys|olidayCheer)|K(?:appa|reygasm)|M(?:rDestructoid|uxy)|NotLikeThis|P(?:arty|ride|JSalt)|RIPCheer|S(?:coops|h(?:owLove|amrock)|eemsGood|wiftRage|treamlabs)|TriHard|uni|VoHiYo))[1-9]\d?\d?\d?\d?\d?\d?(?=\s|$)",
            RegexOptions.Compiled);

        private enum DownloadType
        {
            Clip,
            Video
        }

        public ChatDownloader(ChatDownloadOptions DownloadOptions)
        {
            downloadOptions = DownloadOptions;
            downloadOptions.TempFolder = Path.Combine(
                string.IsNullOrWhiteSpace(downloadOptions.TempFolder) ? Path.GetTempPath() : downloadOptions.TempFolder,
                "TwitchDownloader");
        }

        private static async Task<List<Comment>> DownloadSection(double videoStart, double videoEnd, string videoId,
            IProgress<ProgressReport> progress, ChatFormat format, CancellationToken cancellationToken)
        {
            var comments = new List<Comment>();
            //GQL only wants ints
            videoStart = Math.Floor(videoStart);
            double videoDuration = videoEnd - videoStart;
            double latestMessage = videoStart - 1;
            bool isFirst = true;
            string cursor = "";
            int errorCount = 0;

            List<GqlCommentResponse> commentResponse;
            while (latestMessage < videoEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                commentResponse = null;
                try
                {
                    var request = new HttpRequestMessage()
                    {
                        Method = HttpMethod.Post
                    };


                    if (isFirst)
                    {
                        request.Content = new StringContent(
                            "[{\"operationName\":\"VideoCommentsByOffsetOrCursor\",\"variables\":{\"videoID\":\"" +
                            videoId + "\",\"contentOffsetSeconds\":" + videoStart +
                            "},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a\"}}}]",
                            Encoding.UTF8, "application/json");
                    }
                    else
                    {
                        request.Content = new StringContent(
                            "[{\"operationName\":\"VideoCommentsByOffsetOrCursor\",\"variables\":{\"videoID\":\"" +
                            videoId + "\",\"cursor\":\"" + cursor +
                            "\"},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a\"}}}]",
                            Encoding.UTF8, "application/json");
                    }

                    using (var httpResponse = await httpClient
                               .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                               .ConfigureAwait(false))
                    {
                        httpResponse.EnsureSuccessStatusCode();
                        commentResponse =
                            await httpResponse.Content.ReadFromJsonAsync<List<GqlCommentResponse>>(options: null,
                                cancellationToken);
                    }

                    errorCount = 0;
                }
                catch (HttpRequestException)
                {
                    if (++errorCount > 10)
                    {
                        throw;
                    }

                    await Task.Delay(1_000 * errorCount, cancellationToken);
                    continue;
                }

                if (commentResponse[0].data.video.comments?.edges is null)
                {
                    // video.comments can be null for some dumb reason, skip
                    continue;
                }

                var convertedComments = ConvertComments(commentResponse[0].data.video, format);
                foreach (var comment in convertedComments)
                {
                    if (latestMessage < videoEnd && comment.content_offset_seconds > videoStart)
                        comments.Add(comment);

                    latestMessage = comment.content_offset_seconds;
                }


                if (!commentResponse[0].data.video.comments.pageInfo.hasNextPage)
                    break;
                else
                    cursor = commentResponse[0].data.video.comments.edges.Last().cursor;
                if (progress != null)
                {
                    int percent = (int) Math.Floor((latestMessage - videoStart) / videoDuration * 100);
                    progress.Report(new ProgressReport() {ReportType = ReportType.Percent, Data = percent});
                }

                if (isFirst)
                    isFirst = false;
            }

            return comments;
        }

        private static List<Comment> ConvertComments(CommentVideo video, ChatFormat format)
        {
            List<Comment> returnList = new List<Comment>();

            foreach (var comment in video.comments.edges)
            {
                //Commenter can be null for some reason, skip (deleted account?)
                if (comment.node.commenter == null)
                    continue;
                var oldComment = comment.node;
                var newComment = new Comment
                {
                    _id = oldComment.id,
                    created_at = oldComment.createdAt,
                    channel_id = video.creator.id,
                    content_type = "video",
                    content_id = video.id,
                    content_offset_seconds = oldComment.contentOffsetSeconds
                };
                var commenter = new Commenter
                {
                    display_name = oldComment.commenter.displayName,
                    _id = oldComment.commenter.id,
                    name = oldComment.commenter.login
                };
                newComment.commenter = commenter;
                var message = new Message();

                var bodyStringBuilder = new StringBuilder();
                if (format == ChatFormat.Text)
                {
                    foreach (var fragment in oldComment.message.fragments.Where(fragment => fragment.text != null))
                    {
                        bodyStringBuilder.Append(fragment.text);
                    }
                }
                else
                {
                    var fragments = new List<Fragment>();
                    var emoticons = new List<Emoticon2>();
                    foreach (var fragment in oldComment.message.fragments)
                    {
                        var newFragment = new Fragment();
                        if (fragment.text != null)
                            bodyStringBuilder.Append(fragment.text);

                        if (fragment.emote != null)
                        {
                            newFragment.emoticon = new Emoticon
                            {
                                emoticon_id = fragment.emote.emoteID
                            };

                            var newEmote = new Emoticon2
                            {
                                _id = fragment.emote.emoteID,
                                begin = fragment.emote.from
                            };
                            newEmote.end = newEmote.begin + fragment.text.Length + 1;
                            emoticons.Add(newEmote);
                        }

                        newFragment.text = fragment.text;
                        fragments.Add(newFragment);
                    }

                    message.fragments = fragments;
                    message.emoticons = emoticons;
                    var badges = new List<UserBadge>();
                    foreach (var badge in oldComment.message.userBadges)
                    {
                        if (string.IsNullOrEmpty(badge.setID) && string.IsNullOrEmpty(badge.version))
                            continue;

                        var newBadge = new UserBadge
                        {
                            _id = badge.setID,
                            version = badge.version
                        };
                        badges.Add(newBadge);
                    }

                    message.user_badges = badges;
                    message.user_color = oldComment.message.userColor;
                }

                message.body = bodyStringBuilder.ToString();

                var bitMatch = _bitsRegex.Match(message.body);
                if (bitMatch.Success && int.TryParse(bitMatch.ValueSpan, out var result))
                {
                    message.bits_spent = result;
                }

                newComment.message = message;

                returnList.Add(newComment);
            }

            return returnList;
        }

        public async Task DownloadAsync(IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(downloadOptions.Id))
            {
                throw new NullReferenceException("Null or empty video/clip ID");
            }

            DownloadType downloadType = downloadOptions.Id.All(char.IsDigit) ? DownloadType.Video : DownloadType.Clip;

            var comments = new List<Comment>();
            ChatRoot chatRoot = new()
            {
                FileInfo = new() {Version = ChatRootVersion.CurrentVersion, CreatedAt = DateTime.Now}, streamer = new(),
                video = new(), comments = comments
            };

            string videoId = downloadOptions.Id;
            string videoTitle;
            DateTime videoCreatedAt;
            double videoStart = 0.0;
            double videoEnd = 0.0;
            double videoDuration = 0.0;
            double videoTotalLength;
            int connectionCount = downloadOptions.ConnectionCount;

            if (downloadType == DownloadType.Video)
            {
                GqlVideoResponse videoInfoResponse = await TwitchHelper.GetVideoInfo(int.Parse(videoId));
                if (videoInfoResponse.data.video == null)
                {
                    throw new NullReferenceException("Invalid VOD, deleted/expired VOD possibly?");
                }

                chatRoot.streamer.name = videoInfoResponse.data.video.owner.displayName;
                chatRoot.streamer.id = int.Parse(videoInfoResponse.data.video.owner.id);
                videoTitle = videoInfoResponse.data.video.title;
                videoCreatedAt = videoInfoResponse.data.video.createdAt;
                videoStart = downloadOptions.CropBeginning ? downloadOptions.CropBeginningTime : 0.0;
                videoEnd = downloadOptions.CropEnding
                    ? downloadOptions.CropEndingTime
                    : videoInfoResponse.data.video.lengthSeconds;
                videoTotalLength = videoInfoResponse.data.video.lengthSeconds;

                GqlVideoChapterResponse videoChapterResponse = await TwitchHelper.GetVideoChapters(int.Parse(videoId));
                foreach (var responseChapter in videoChapterResponse.data.video.moments.edges)
                {
                    VideoChapter chapter = new()
                    {
                        id = responseChapter.node.id,
                        startMilliseconds = responseChapter.node.positionMilliseconds,
                        lengthMilliseconds = responseChapter.node.durationMilliseconds,
                        _type = responseChapter.node._type,
                        description = responseChapter.node.description,
                        subDescription = responseChapter.node.subDescription,
                        thumbnailUrl = responseChapter.node.thumbnailURL,
                        gameId = responseChapter.node.details.game?.id ?? null,
                        gameDisplayName = responseChapter.node.details.game?.displayName ?? null,
                        gameBoxArtUrl = responseChapter.node.details.game?.boxArtURL ?? null
                    };
                    chatRoot.video.chapters.Add(chapter);
                }
            }
            else
            {
                GqlClipResponse clipInfoResponse = await TwitchHelper.GetClipInfo(videoId);
                if (clipInfoResponse.data.clip.video == null || clipInfoResponse.data.clip.videoOffsetSeconds == null)
                {
                    throw new NullReferenceException("Invalid VOD for clip, deleted/expired VOD possibly?");
                }

                videoId = clipInfoResponse.data.clip.video.id;
                downloadOptions.CropBeginning = true;
                downloadOptions.CropBeginningTime = (int) clipInfoResponse.data.clip.videoOffsetSeconds;
                downloadOptions.CropEnding = true;
                downloadOptions.CropEndingTime =
                    downloadOptions.CropBeginningTime + clipInfoResponse.data.clip.durationSeconds;
                chatRoot.streamer.name = clipInfoResponse.data.clip.broadcaster.displayName;
                chatRoot.streamer.id = int.Parse(clipInfoResponse.data.clip.broadcaster.id);
                videoTitle = clipInfoResponse.data.clip.title;
                videoCreatedAt = clipInfoResponse.data.clip.createdAt;
                videoStart = (int) clipInfoResponse.data.clip.videoOffsetSeconds;
                videoEnd = (int) clipInfoResponse.data.clip.videoOffsetSeconds +
                           clipInfoResponse.data.clip.durationSeconds;
                videoTotalLength = clipInfoResponse.data.clip.durationSeconds;
                connectionCount = 1;
            }

            chatRoot.video.id = videoId;
            chatRoot.video.title = videoTitle;
            chatRoot.video.created_at = videoCreatedAt;
            chatRoot.video.start = videoStart;
            chatRoot.video.end = videoEnd;
            chatRoot.video.length = videoTotalLength;
            videoDuration = videoEnd - videoStart;

            var commentsSet = new SortedSet<Comment>(new SortedCommentComparer());
            var tasks = new List<Task<List<Comment>>>();
            var percentages = new int[connectionCount];


            double chunk = videoDuration / connectionCount;
            for (int i = 0; i < connectionCount; i++)
            {
                int tc = i;

                Progress<ProgressReport> taskProgress = null;
                if (!downloadOptions.Quiet)
                {
                    taskProgress = new Progress<ProgressReport>(progressReport =>
                    {
                        if (progressReport.ReportType != ReportType.Percent)
                        {
                            progress.Report(progressReport);
                        }
                        else
                        {
                            int percent = (int) progressReport.Data;
                            if (percent > 100)
                            {
                                percent = 100;
                            }

                            percentages[tc] = percent;

                            percent = 0;
                            for (int j = 0; j < connectionCount; j++)
                            {
                                if (j < percentages.Length)
                                    percent += percentages[j];
                            }

                            percent /= connectionCount;

                            progress.Report(new ProgressReport()
                                {ReportType = ReportType.SameLineStatus, Data = $"Downloading {percent}%"});
                            progress.Report(new ProgressReport() {ReportType = ReportType.Percent, Data = percent});
                        }
                    });
                }

                double start = videoStart + chunk * i;
                tasks.Add(DownloadSection(start, start + chunk, videoId, taskProgress, downloadOptions.DownloadFormat,
                    cancellationToken));
            }

            await Task.WhenAll(tasks);
            foreach (var comment in tasks.Select(task => task.Result).SelectMany(result => result))
            {
                commentsSet.Add(comment);
            }


            comments = commentsSet.DistinctBy(x => x._id).ToList();
            chatRoot.comments = comments;

            if (downloadOptions.EmbedData && (downloadOptions.DownloadFormat is ChatFormat.Json or ChatFormat.Html))
            {
                progress.Report(new ProgressReport()
                    {ReportType = ReportType.NewLineStatus, Data = "Downloading + Embedding Images"});
                chatRoot.embeddedData = new EmbeddedData();

                // This is the exact same process as in ChatUpdater.cs but not in a task oriented manner
                // TODO: Combine this with ChatUpdater in a different file
                List<TwitchEmote> thirdPartyEmotes = await TwitchHelper.GetThirdPartyEmotes(comments,
                    chatRoot.streamer.id, downloadOptions.TempFolder, bttv: downloadOptions.BttvEmotes,
                    ffz: downloadOptions.FfzEmotes, stv: downloadOptions.StvEmotes,
                    cancellationToken: cancellationToken);
                List<TwitchEmote> firstPartyEmotes = await TwitchHelper.GetEmotes(comments, downloadOptions.TempFolder,
                    cancellationToken: cancellationToken);
                List<ChatBadge> twitchBadges = await TwitchHelper.GetChatBadges(comments, chatRoot.streamer.id,
                    downloadOptions.TempFolder, cancellationToken: cancellationToken);
                List<CheerEmote> twitchBits = await TwitchHelper.GetBits(comments, downloadOptions.TempFolder,
                    chatRoot.streamer.id.ToString(), cancellationToken: cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                foreach (TwitchEmote emote in thirdPartyEmotes)
                {
                    EmbedEmoteData newEmote = new EmbedEmoteData();
                    newEmote.id = emote.Id;
                    newEmote.imageScale = emote.ImageScale;
                    newEmote.data = emote.ImageData;
                    newEmote.name = emote.Name;
                    newEmote.width = emote.Width / emote.ImageScale;
                    newEmote.height = emote.Height / emote.ImageScale;
                    chatRoot.embeddedData.thirdParty.Add(newEmote);
                }

                foreach (TwitchEmote emote in firstPartyEmotes)
                {
                    EmbedEmoteData newEmote = new EmbedEmoteData();
                    newEmote.id = emote.Id;
                    newEmote.imageScale = emote.ImageScale;
                    newEmote.data = emote.ImageData;
                    newEmote.width = emote.Width / emote.ImageScale;
                    newEmote.height = emote.Height / emote.ImageScale;
                    chatRoot.embeddedData.firstParty.Add(newEmote);
                }

                foreach (ChatBadge badge in twitchBadges)
                {
                    EmbedChatBadge newBadge = new EmbedChatBadge();
                    newBadge.name = badge.Name;
                    newBadge.versions = badge.VersionsData;
                    chatRoot.embeddedData.twitchBadges.Add(newBadge);
                }

                foreach (CheerEmote bit in twitchBits)
                {
                    EmbedCheerEmote newBit = new EmbedCheerEmote();
                    newBit.prefix = bit.prefix;
                    newBit.tierList = new Dictionary<int, EmbedEmoteData>();
                    foreach (KeyValuePair<int, TwitchEmote> emotePair in bit.tierList)
                    {
                        EmbedEmoteData newEmote = new EmbedEmoteData();
                        newEmote.id = emotePair.Value.Id;
                        newEmote.imageScale = emotePair.Value.ImageScale;
                        newEmote.data = emotePair.Value.ImageData;
                        newEmote.name = emotePair.Value.Name;
                        newEmote.width = emotePair.Value.Width / emotePair.Value.ImageScale;
                        newEmote.height = emotePair.Value.Height / emotePair.Value.ImageScale;
                        newBit.tierList.Add(emotePair.Key, newEmote);
                    }

                    chatRoot.embeddedData.twitchBits.Add(newBit);
                }
            }

            if (downloadOptions.DownloadFormat is ChatFormat.Json)
            {
                //Best effort, but if we fail oh well
                progress.Report(new ProgressReport()
                    {ReportType = ReportType.NewLineStatus, Data = "Backfilling commenter info"});
                List<string> userList = chatRoot.comments.DistinctBy(x => x.commenter._id).Select(x => x.commenter._id)
                    .ToList();
                Dictionary<string, User> userInfo = new Dictionary<string, User>();
                int batchSize = 100;
                bool failedInfo = false;
                for (int i = 0; i <= userList.Count / batchSize; i++)
                {
                    try
                    {
                        List<string> userSubset = userList.Skip(i * batchSize).Take(batchSize).ToList();
                        GqlUserInfoResponse userInfoResponse = await TwitchHelper.GetUserInfo(userSubset);
                        foreach (var user in userInfoResponse.data.users)
                        {
                            userInfo[user.id] = user;
                        }
                    }
                    catch
                    {
                        failedInfo = true;
                    }
                }

                if (failedInfo)
                {
                    progress.Report(new ProgressReport()
                        {ReportType = ReportType.Log, Data = "Failed to backfill some commenter info"});
                }

                foreach (var comment in chatRoot.comments)
                {
                    if (userInfo.TryGetValue(comment.commenter._id, out var user))
                    {
                        comment.commenter.updated_at = user.updatedAt;
                        comment.commenter.created_at = user.createdAt;
                        comment.commenter.bio = user.description;
                        comment.commenter.logo = user.profileImageURL;
                    }
                }
            }

            progress.Report(new ProgressReport(ReportType.NewLineStatus, "Writing output file"));
            switch (downloadOptions.DownloadFormat)
            {
                case ChatFormat.Json:
                    await ChatJson.SerializeAsync(downloadOptions.Filename, chatRoot, downloadOptions.Compression,
                        cancellationToken);
                    break;
                case ChatFormat.Html:
                    await ChatHtml.SerializeAsync(downloadOptions.Filename, chatRoot, downloadOptions.EmbedData,
                        cancellationToken);
                    break;
                case ChatFormat.Text:
                    await ChatText.SerializeAsync(downloadOptions.Filename, chatRoot, downloadOptions.TimeFormat);
                    break;
                default:
                    throw new NotImplementedException("Requested output chat format is not implemented");
            }
        }
    }
}