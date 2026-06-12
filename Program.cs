using Discord;
using Discord.WebSocket;
using Discord.Net;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode.Search;
using YoutubeExplode.Playlists;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;


class Program
{
    private static DiscordSocketClient? _client;
    private static YoutubeClient? _youtube;
    private static readonly Dictionary<ulong, Queue<DownloadRequest>> UserQueues = new();
    private static readonly Dictionary<ulong, DateTime> UserLastDownload = new();
    private static readonly Dictionary<string, DownloadStats> DownloadHistory = new();
    private static readonly string HistoryFile = "download_history.json";
    private static readonly string TempDirectory = "temp_downloads";
    
    private static readonly string? TOKEN = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
    private static readonly string? YOUTUBE_COOKIES = Environment.GetEnvironmentVariable("YOUTUBE_COOKIES");
        
    private const long MAX_FILE_SIZE = 8 * 1024 * 1024;
    private const long MAX_FILE_SIZE_BOOST_TIER_3 = 100 * 1024 * 1024;
    private const int MAX_DOWNLOADS_PER_HOUR = 10;
    
    static async Task Main(string[] args)
    {
        Directory.CreateDirectory(TempDirectory);
        
        if (string.IsNullOrWhiteSpace(TOKEN))
        {
            Console.WriteLine("ERROR: Debes configurar la variable de entorno DISCORD_BOT_TOKEN en Railway.");
            return;
        }
        
        _youtube = CreateYoutubeClient();
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
            ConnectionTimeout = 30000
        });

        _client.Log += LogAsync;
        _client.SlashCommandExecuted += SlashCommandHandlerAsync;
        _client.ButtonExecuted += ButtonHandlerAsync;
        _client.SelectMenuExecuted += SelectMenuHandlerAsync;
        _client.Ready += ReadyAsync;

        LoadHistory();

        await _client.LoginAsync(TokenType.Bot, TOKEN!);
        await _client.StartAsync();

        Console.WriteLine("?? Bot Premium iniciado con todas las funciones!");
        await Task.Delay(-1);
    }

        private static YoutubeClient CreateYoutubeClient()
    {
        var httpClientHandler = new HttpClientHandler();
        var httpClient = new HttpClient(httpClientHandler);
        
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
        
        if (!string.IsNullOrWhiteSpace(YOUTUBE_COOKIES))
        {
            try
            {
                var cookies = YOUTUBE_COOKIES.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(cookie => cookie.Trim())
                    .Select(cookie =>
                    {
                        var parts = cookie.Split('=', 2);
                        return new Cookie(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : string.Empty, "/", ".youtube.com");
                    })
                    .ToArray();

                return new YoutubeClient(httpClient, cookies);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: No se pudieron parsear las cookies de YouTube: {ex.Message}");
            }
        }

        Console.WriteLine("WARNING: Iniciando YoutubeClient con HttpClient personalizado.");
        return new YoutubeClient(httpClient);
    }

    private static Task LogAsync(LogMessage log)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{log.Severity}] {log.Message}");
        if (log.Exception != null)
            Console.WriteLine($"Exception: {log.Exception}");
        return Task.CompletedTask;
    }

    private static async Task ReadyAsync()
    {
        var commands = new List<SlashCommandProperties>
        {
            new SlashCommandBuilder()
                .WithName("download")
                .WithDescription("Descarga video o audio con men� de calidad")
                .AddOption("url", ApplicationCommandOptionType.String, "URL de YouTube", isRequired: true)
                .Build(),

            new SlashCommandBuilder()
                .WithName("search")
                .WithDescription("Busca videos con botones de descarga")
                .AddOption("query", ApplicationCommandOptionType.String, "T�rmino de b�squeda", isRequired: true)
                .Build(),

            new SlashCommandBuilder()
                .WithName("playlist")
                .WithDescription("Descarga toda una playlist (ZIP si cabe)")
                .AddOption("url", ApplicationCommandOptionType.String, "URL de la playlist", isRequired: true)
                .Build(),

            new SlashCommandBuilder()
                .WithName("info")
                .WithDescription("Informaci�n detallada del video")
                .AddOption("url", ApplicationCommandOptionType.String, "URL del video", isRequired: true)
                .Build(),

            new SlashCommandBuilder()
                .WithName("queue")
                .WithDescription("Muestra tu cola de descargas")
                .Build(),

            new SlashCommandBuilder()
                .WithName("cancel")
                .WithDescription("Cancela tus descargas pendientes")
                .Build(),

            new SlashCommandBuilder()
                .WithName("stats")
                .WithDescription("Estad�sticas completas del servidor")
                .Build(),

            new SlashCommandBuilder()
                .WithName("top")
                .WithDescription("Top 10 videos m�s descargados")
                .Build(),

            new SlashCommandBuilder()
                .WithName("lyrics")
                .WithDescription("Busca letras de canciones")
                .AddOption("cancion", ApplicationCommandOptionType.String, "Nombre de la canci�n", isRequired: true)
                .AddOption("artista", ApplicationCommandOptionType.String, "Nombre del artista", isRequired: false)
                .Build(),

            new SlashCommandBuilder()
                .WithName("thumbnail")
                .WithDescription("Descarga la miniatura del video")
                .AddOption("url", ApplicationCommandOptionType.String, "URL del video", isRequired: true)
                .AddOption("calidad", ApplicationCommandOptionType.String, "max, hd, sd", isRequired: false)
                .Build(),

            new SlashCommandBuilder()
                .WithName("favorites")
                .WithDescription("Muestra tus videos favoritos")
                .Build(),

            new SlashCommandBuilder()
                .WithName("history")
                .WithDescription("Tu historial de descargas recientes")
                .Build()
        };

        try
        {
            await _client!.BulkOverwriteGlobalApplicationCommandsAsync(commands.ToArray());
            Console.WriteLine($"? {commands.Count} comandos registrados!");
        }
        catch (HttpException ex)
        {
            Console.WriteLine($"? Error al registrar comandos: {ex.Message}");
        }
    }

    private static async Task SlashCommandHandlerAsync(SocketSlashCommand command)
    {
        try 
        {
            await command.DeferAsync(ephemeral: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error en Defer: {ex.Message}");
            return;
        }
        
        _ = Task.Run(async () => 
        {
            try
            {
                switch (command.Data.Name)
                {
                    case "download":
                        await HandleDownloadMenuAsync(command);
                        break;
                    case "search":
                        await HandleSearchWithButtonsAsync(command);
                        break;
                    case "playlist":
                        await HandlePlaylistAsync(command);
                        break;
                    case "info":
                        await HandleInfoAsync(command);
                        break;
                    case "queue":
                        await HandleQueueAsync(command);
                        break;
                    case "cancel":
                        await HandleCancelAsync(command);
                        break;
                    case "stats":
                        await HandleFullStatsAsync(command);
                        break;
                    case "top":
                        await HandleTopDownloadsAsync(command);
                        break;
                    case "lyrics":
                        await HandleLyricsAsync(command);
                        break;
                    case "thumbnail":
                        await HandleThumbnailAsync(command);
                        break;
                    case "favorites":
                        await HandleFavoritesAsync(command);
                        break;
                    case "history":
                        await HandleHistoryAsync(command);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error procesando {command.Data.Name}: {ex}");
                try 
                {
                    await SendErrorEmbedAsync(command, "Error", ex.Message);
                }
                catch { }
            }
        });
    }

    private static async Task HandleSearchWithButtonsAsync(SocketSlashCommand command)
    {
        var query = command.Data.Options.First().Value as string;
        if (string.IsNullOrWhiteSpace(query))
        {
            await SendErrorEmbedAsync(command, "Consulta vac�a", "Proporciona un t�rmino de b�squeda.");
            return;
        }

        try
        {
            var results = new List<ISearchResult>();
            await foreach (var result in _youtube!.Search.GetVideosAsync(query).Take(5))
            {
                results.Add(result);
            }

            if (results.Count == 0)
            {
                await command.FollowupAsync("?? No se encontraron resultados.");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle($"?? Resultados para: {query}")
                .WithColor(Color.Blue);

            var comp = new ComponentBuilder();
            int i = 1;
            
            foreach (var searchResult in results.OfType<VideoSearchResult>())
            {
                var duration = searchResult.Duration?.ToString(@"mm\:ss") ?? "??:??";
                embed.AddField($"#{i} {searchResult.Title}", 
                    $"{searchResult.Author.ChannelTitle} � {duration}", false);
                
                // Usar | como separador en lugar de _ para evitar conflictos con IDs de YouTube
                comp.WithButton($"Descargar #{i}", 
                    customId: $"searchdl|{command.User.Id}|{searchResult.Id}", 
                    ButtonStyle.Primary);
                i++;
            }

            await command.FollowupAsync(embed: embed.Build(), components: comp.Build());
        }
        catch (Exception ex)
        {
            var message = ex.Message.Contains("Exceeded request rate limit")
                ? "YouTube est� limitando las peticiones. Intenta de nuevo m�s tarde o configura `YOUTUBE_COOKIES` con cookies de un usuario autenticado."
                : ex.Message;
            await SendErrorEmbedAsync(command, "Error de b�squeda", message);
        }
    }

    private static async Task HandleDownloadMenuAsync(SocketSlashCommand command)
    {
        var url = command.Data.Options.First().Value as string;
        if (string.IsNullOrEmpty(url) || (!url.Contains("youtube.com") && !url.Contains("youtu.be")))
        {
            await SendErrorEmbedAsync(command, "URL Inv�lida", "Proporciona una URL v�lida de YouTube.");
            return;
        }

        try
        {
            var videoId = ExtractVideoId(url);
            if (string.IsNullOrEmpty(videoId))
            {
                await SendErrorEmbedAsync(command, "URL Inv�lida", "No se pudo extraer el ID del video.");
                return;
            }

            var video = await _youtube!.Videos.GetAsync(videoId);
            
            var embed = new EmbedBuilder()
                .WithTitle("?? Selecciona Formato y Calidad")
                .WithDescription($"**{video.Title}**\n?? {video.Duration?.ToString(@"mm\:ss")} � ?? {video.Author.ChannelTitle}")
                .WithThumbnailUrl(video.Thumbnails.FirstOrDefault()?.Url)
                .WithColor(Color.Gold)
                .Build();

            // Usar | como separador en lugar de _
            var menu = new SelectMenuBuilder()
                .WithCustomId($"download|{command.User.Id}|{video.Id}")
                .WithPlaceholder("??? Selecciona calidad...")
                .AddOption("?? MP3 - Audio (Alta)", "mp3_high", "Mejor calidad disponible")
                .AddOption("?? MP3 - Audio (Media)", "mp3_medium", "Calidad est�ndar")
                .AddOption("?? MP4 - Video 1080p", "mp4_1080", "Alta definici�n")
                .AddOption("?? MP4 - Video 720p", "mp4_720", "HD est�ndar")
                .AddOption("?? MP4 - Video 480p", "mp4_480", "Calidad media")
                .AddOption("?? MP4 - Video 360p", "mp4_360", "Para ahorrar datos");

            var component = new ComponentBuilder()
                .WithSelectMenu(menu)
                .Build();

            await command.FollowupAsync(embed: embed, components: component);
        }
        catch (Exception ex)
        {
            await SendErrorEmbedAsync(command, "Error", $"No se pudo obtener informaci�n del video: {ex.Message}");
        }
    }

    private static string? ExtractVideoId(string url)
    {
        if (url.Contains("youtu.be"))
        {
            return url.Split('/').Last().Split('?').First();
        }
        
        var match = Regex.Match(url, @"[?&]v=([^&]+)");
        if (match.Success) return match.Groups[1].Value;
        
        match = Regex.Match(url, @"shorts/([^/?]+)");
        if (match.Success) return match.Groups[1].Value;
        
        return null;
    }

    private static async Task SelectMenuHandlerAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith("download|")) return;
        
        await component.DeferAsync();
        
        // Usar | como separador - los IDs de YouTube nunca tienen |
        var parts = component.Data.CustomId.Split('|');
        if (parts.Length != 3)
        {
            await component.FollowupAsync("? Error en el formato del men�.", ephemeral: true);
            return;
        }
        
        var userId = ulong.Parse(parts[1]);
        var videoId = parts[2]; // Ahora s� es el ID completo
        
        if (component.User.Id != userId)
        {
            await component.FollowupAsync("? Este men� no es tuyo.", ephemeral: true);
            return;
        }

        var selection = component.Data.Values.First();
        
        var (format, quality) = selection.Split('_') switch
        {
            ["mp3", var q] => ("mp3", q),
            ["mp4", var q] => ("mp4", q),
            _ => ("mp3", "high")
        };

        var url = $"https://youtube.com/watch?v={videoId}";
        
        var request = new DownloadRequest
        {
            UserId = component.User.Id,
            Url = url,
            VideoId = videoId,
            Format = format,
            Quality = quality,
            Channel = component.Channel,
            RequestedAt = DateTime.UtcNow
        };

        if (!UserQueues.ContainsKey(component.User.Id))
            UserQueues[component.User.Id] = new Queue<DownloadRequest>();
        
        UserQueues[component.User.Id].Enqueue(request);

        await component.ModifyOriginalResponseAsync(msg => 
        {
            msg.Components = null;
            msg.Embed = new EmbedBuilder()
                .WithTitle("? Agregado a la Cola")
                .WithDescription($"Posici�n: **#{UserQueues[component.User.Id].Count}**\n? Procesando...")
                .WithColor(Color.Green)
                .Build();
        });

        if (UserQueues[component.User.Id].Count == 1)
        {
            _ = Task.Run(async () => 
            {
                try
                {
                    await ProcessDownloadAsync(request, component);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error en ProcessDownload: {ex}");
                    await component.Channel.SendMessageAsync($"? Error procesando descarga: {ex.Message}");
                }
                finally
                {
                    if (UserQueues.ContainsKey(request.UserId) && UserQueues[request.UserId].Count > 0)
                    {
                        UserQueues[request.UserId].Dequeue();
                        if (UserQueues[request.UserId].Count > 0)
                        {
                            var next = UserQueues[request.UserId].Peek();
                            await ProcessDownloadAsync(next, null);
                        }
                    }
                }
            });
        }
    }

    private static async Task ProcessDownloadAsync(DownloadRequest request, SocketMessageComponent? component)
    {
        var statusMessage = component != null 
            ? await request.Channel.SendMessageAsync($"? Descargando **{request.VideoId}**...")
            : null;

        try
        {
            // Verificar que el ID sea v�lido antes de usarlo
            if (string.IsNullOrWhiteSpace(request.VideoId) || request.VideoId.Length < 10)
            {
                throw new Exception($"ID de video inv�lido: '{request.VideoId}'");
            }

            var video = await _youtube!.Videos.GetAsync(request.VideoId);
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(request.VideoId);
            
            string filePath;
            string fileName;
            long fileSize;

            if (request.Format == "mp3")
            {
                var streamInfo = request.Quality == "high" 
                    ? streamManifest.GetAudioOnlyStreams().Cast<IStreamInfo>().GetWithHighestBitrate()
                    : streamManifest.GetAudioOnlyStreams().FirstOrDefault();
                
                if (streamInfo == null)
                    throw new Exception("No se encontró stream de audio disponible");

                fileName = $"{SanitizeFileName(video.Title)}.mp3";
                filePath = Path.Combine(TempDirectory, fileName);
                
                await _youtube.Videos.Streams.DownloadAsync(streamInfo, filePath);
                fileSize = new FileInfo(filePath).Length;
            }
            else
            {
                var muxedStreams = streamManifest.GetMuxedStreams();
                IStreamInfo? muxedStreamInfo = request.Quality switch
                {
                    "1080" => muxedStreams.FirstOrDefault(s => s.VideoQuality.Label.StartsWith("1080")),
                    "720" => muxedStreams.FirstOrDefault(s => s.VideoQuality.Label.StartsWith("720")),
                    "480" => muxedStreams.FirstOrDefault(s => s.VideoQuality.Label.StartsWith("480")),
                    "360" => muxedStreams.FirstOrDefault(s => s.VideoQuality.Label.StartsWith("360")),
                    _ => null
                };

                if (muxedStreamInfo == null)
                    muxedStreamInfo = muxedStreams.GetWithHighestBitrate();

                if (muxedStreamInfo == null)
                    throw new Exception("No se encontró stream muxed disponible con audio para MP4.");

                fileName = $"{SanitizeFileName(video.Title)}.mp4";
                filePath = Path.Combine(TempDirectory, fileName);
                
                await _youtube.Videos.Streams.DownloadAsync(muxedStreamInfo, filePath);
                fileSize = new FileInfo(filePath).Length;
            }

            var maxSize = GetMaxFileSize(request.Channel);
            if (fileSize > maxSize)
            {
                File.Delete(filePath);
                await request.Channel.SendMessageAsync(
                    $"? El archivo ({FormatBytes(fileSize)}) excede el l�mite de {FormatBytes(maxSize)}.");
                return;
            }

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            await request.Channel.SendFileAsync(fileStream, fileName, 
                $"? **{video.Title}** descargado correctamente!");

            if (!DownloadHistory.ContainsKey(video.Id))
            {
                DownloadHistory[video.Id] = new DownloadStats 
                { 
                    Title = video.Title, 
                    UserId = request.UserId,
                    Date = DateTime.UtcNow 
                };
            }
            DownloadHistory[video.Id].Count++;
            DownloadHistory[video.Id].Date = DateTime.UtcNow;
            SaveHistory();

            fileStream.Close();
            File.Delete(filePath);

            if (statusMessage != null)
                await statusMessage.DeleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error en descarga: {ex}");
            var userMessage = ex.Message.Contains("Exceeded request rate limit")
                ? "? YouTube est� limitando las peticiones. Intenta de nuevo m�s tarde o configura `YOUTUBE_COOKIES` con cookies de un usuario autenticado."
                : $"? Error en la descarga: {ex.Message}";
            await request.Channel.SendMessageAsync(userMessage);
            if (statusMessage != null)
                await statusMessage.DeleteAsync();
        }
    }

    private static long GetMaxFileSize(ISocketMessageChannel channel)
    {
        if (channel is SocketGuildChannel guildChannel)
        {
            var guild = guildChannel.Guild;
            return guild.PremiumTier switch
            {
                PremiumTier.Tier3 => MAX_FILE_SIZE_BOOST_TIER_3,
                PremiumTier.Tier2 => 50 * 1024 * 1024,
                PremiumTier.Tier1 => 25 * 1024 * 1024,
                _ => MAX_FILE_SIZE
            };
        }
        return MAX_FILE_SIZE;
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');
        return fileName.Length > 100 ? fileName.Substring(0, 100) : fileName;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private static async Task ButtonHandlerAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith("searchdl|")) return;
        
        await component.DeferAsync(ephemeral: true);
        
        // Usar | como separador
        var parts = component.Data.CustomId.Split('|');
        if (parts.Length != 3)
        {
            await component.FollowupAsync("? Error en el formato del bot�n.", ephemeral: true);
            return;
        }
        
        var userId = ulong.Parse(parts[1]);
        var videoId = parts[2]; // ID completo
        
        if (component.User.Id != userId)
        {
            await component.FollowupAsync("? Este bot�n no es tuyo.", ephemeral: true);
            return;
        }

        var url = $"https://youtube.com/watch?v={videoId}";
        
        await component.FollowupAsync(
            $"? **Video seleccionado!**\nUsa `/download url:{url}` para descargarlo.", 
            ephemeral: true);
    }

    private static async Task HandlePlaylistAsync(SocketSlashCommand command)
    {
        await command.FollowupAsync("?? Funci�n de playlist en desarrollo. Usa `/download` para videos individuales.");
    }

    private static async Task HandleFullStatsAsync(SocketSlashCommand command)
    {
        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        var totalDownloads = DownloadHistory.Count;
        var todayDownloads = DownloadHistory.Count(x => x.Value.Date.Date == DateTime.UtcNow.Date);
        var topUser = DownloadHistory.GroupBy(x => x.Value.UserId)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        
        var embed = new EmbedBuilder()
            .WithTitle("?? Estad�sticas del Servidor")
            .WithDescription($"**{guild?.Name ?? "Servidor"}**")
            .AddField("?? Nivel de Boost", GetBoostTierName(guild?.PremiumTier), true)
            .AddField("?? L�mite de Archivos", FormatBytes(GetMaxFileSize(command.Channel)), true)
            .AddField("?? Rate Limit", $"{MAX_DOWNLOADS_PER_HOUR} descargas/hora por usuario", true)
            .AddField("?? Descargas Totales", totalDownloads.ToString(), true)
            .AddField("?? Descargas Hoy", todayDownloads.ToString(), true)
            .AddField("?? Usuario Top", (topUser != null && topUser.Key != 0) ? $"<@{topUser.Key}> ({topUser.Count()} desc.)" : "Nadie", true)
            .AddField("?? Miembros", guild?.MemberCount.ToString() ?? "?", true)
            .AddField("?? Boosts", guild?.PremiumSubscriptionCount.ToString() ?? "0", true)
            .AddField("?? Creado", guild?.CreatedAt.ToString("dd/MM/yyyy") ?? "?", true)
            .WithThumbnailUrl(guild?.IconUrl ?? command.User.GetAvatarUrl())
            .WithColor(Color.Gold)
            .WithFooter($"Solicitado por {command.User.Username}")
            .WithTimestamp(DateTimeOffset.Now)
            .Build();

        await command.FollowupAsync(embed: embed);
    }

    private static async Task HandleTopDownloadsAsync(SocketSlashCommand command)
    {
        var top = DownloadHistory
            .OrderByDescending(x => x.Value.Count)
            .Take(10)
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle("?? Top 10 Videos M�s Descargados")
            .WithColor(Color.Gold);

        if (top.Count == 0)
        {
            embed.WithDescription("A�n no hay descargas registradas.");
        }
        else
        {
            int pos = 1;
            foreach (var item in top)
            {
                embed.AddField($"#{pos} {item.Value.Title}", 
                    $"Descargado **{item.Value.Count}** veces � �ltima: {item.Value.Date:dd/MM/yy}", false);
                pos++;
            }
        }

        await command.FollowupAsync(embed: embed.Build());
    }

    private static async Task HandleLyricsAsync(SocketSlashCommand command)
    {
        var song = command.Data.Options.First().Value as string;
        await command.FollowupAsync($"?? Buscando letras de: **{song}**...\n*(Funci�n en desarrollo)*");
    }

    private static async Task HandleThumbnailAsync(SocketSlashCommand command)
    {
        var url = command.Data.Options.First(o => o.Name == "url").Value as string;
        var calidad = command.Data.Options.FirstOrDefault(o => o.Name == "calidad")?.Value as string ?? "max";
        
        if (string.IsNullOrWhiteSpace(url))
        {
            await SendErrorEmbedAsync(command, "Error", "La URL no puede estar vac�a");
            return;
        }
        
        try
        {
            var videoId = ExtractVideoId(url);
            if (string.IsNullOrEmpty(videoId))
            {
                await SendErrorEmbedAsync(command, "Error", "URL de YouTube inv�lida");
                return;
            }

            var video = await _youtube!.Videos.GetAsync(videoId);
            var thumbnail = calidad switch
            {
                "max" => video.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault(),
                "hd" => video.Thumbnails.FirstOrDefault(t => t.Resolution.Width >= 1280),
                _ => video.Thumbnails.FirstOrDefault()
            };

            var embed = new EmbedBuilder()
                .WithTitle("??? Miniatura del Video")
                .WithDescription($"[{video.Title}]({video.Url})")
                .WithImageUrl(thumbnail?.Url)
                .WithColor(Color.Blue)
                .Build();

            await command.FollowupAsync(embed: embed);
        }
        catch (Exception ex)
        {
            await SendErrorEmbedAsync(command, "Error", ex.Message);
        }
    }

    private static async Task HandleFavoritesAsync(SocketSlashCommand command)
    {
        await command.FollowupAsync("? Funci�n de favoritos en desarrollo.", ephemeral: true);
    }

    private static async Task HandleHistoryAsync(SocketSlashCommand command)
    {
        var userHistory = DownloadHistory
            .Where(x => x.Value.UserId == command.User.Id)
            .OrderByDescending(x => x.Value.Date)
            .Take(5);

        var embed = new EmbedBuilder()
            .WithTitle("?? Tus �ltimas Descargas")
            .WithColor(Color.Blue);

        if (!userHistory.Any())
        {
            embed.WithDescription("No tienes descargas recientes.");
        }
        else
        {
            foreach (var item in userHistory)
            {
                embed.AddField(item.Value.Title, item.Value.Date.ToString("dd/MM/yy HH:mm"), false);
            }
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private static async Task HandleInfoAsync(SocketSlashCommand command)
    {
        var url = command.Data.Options.First().Value as string;
        if (string.IsNullOrEmpty(url))
        {
            await SendErrorEmbedAsync(command, "Error", "URL no v�lida");
            return;
        }
        
        try
        {
            var videoId = ExtractVideoId(url);
            if (string.IsNullOrEmpty(videoId))
            {
                await SendErrorEmbedAsync(command, "Error", "No se pudo extraer el ID del video");
                return;
            }

            var video = await _youtube!.Videos.GetAsync(videoId);
            var embed = new EmbedBuilder()
                .WithTitle("?? Informaci�n del Video")
                .WithDescription($"**[{video.Title}]({video.Url})**")
                .AddField("?? Autor", video.Author.ChannelTitle, true)
                .AddField("?? Duraci�n", video.Duration?.ToString(@"mm\:ss") ?? "N/A", true)
                .AddField("?? Fecha", video.UploadDate.ToString("dd/MM/yyyy"), true)
                .AddField("??? Vistas", FormatNumber(video.Engagement.ViewCount), true)
                .AddField("?? Likes", FormatNumber(video.Engagement.LikeCount), true)
                .AddField("?? ID", $"`{video.Id}`", true)
                .WithThumbnailUrl(video.Thumbnails.FirstOrDefault()?.Url)
                .WithColor(Color.Red)
                .Build();

            await command.FollowupAsync(embed: embed);
        }
        catch (Exception ex)
        {
            await SendErrorEmbedAsync(command, "Error", ex.Message);
        }
    }

    private static async Task HandleQueueAsync(SocketSlashCommand command)
    {
        if (!UserQueues.ContainsKey(command.User.Id) || UserQueues[command.User.Id].Count == 0)
        {
            await command.FollowupAsync("?? Tu cola est� vac�a.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("?? Tu Cola de Descargas")
            .WithDescription($"{UserQueues[command.User.Id].Count} pendiente(s)")
            .WithColor(Color.Blue);

        int pos = 1;
        foreach (var item in UserQueues[command.User.Id])
        {
            embed.AddField($"#{pos}", $"{item.Format.ToUpper()} - {item.Url}", false);
            pos++;
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private static async Task HandleCancelAsync(SocketSlashCommand command)
    {
        if (UserQueues.ContainsKey(command.User.Id))
        {
            var count = UserQueues[command.User.Id].Count;
            UserQueues[command.User.Id].Clear();
            await command.FollowupAsync($"? Canceladas **{count}** descarga(s).", ephemeral: true);
        }
        else
        {
            await command.FollowupAsync("?? No tienes descargas en cola.", ephemeral: true);
        }
    }

    private static async Task SendErrorEmbedAsync(SocketSlashCommand command, string title, string desc)
    {
        try
        {
            var embed = new EmbedBuilder()
                .WithTitle($"? {title}")
                .WithDescription(desc)
                .WithColor(Color.DarkRed)
                .WithTimestamp(DateTimeOffset.Now)
                .Build();
            await command.FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al enviar mensaje de error: {ex}");
        }
    }

    private static string FormatNumber(long num) => num >= 1000000 ? $"{num / 1000000.0:F1}M" : num >= 1000 ? $"{num / 1000.0:F1}K" : num.ToString();
    
    private static string GetBoostTierName(PremiumTier? tier) => tier switch
    {
        PremiumTier.Tier1 => "Nivel 1 (25MB)",
        PremiumTier.Tier2 => "Nivel 2 (50MB)",
        PremiumTier.Tier3 => "Nivel 3 (100MB) ?",
        _ => "Nivel 0 (8MB)"
    };

    private static void LoadHistory()
    {
        if (File.Exists(HistoryFile))
        {
            try
            {
                var json = File.ReadAllText(HistoryFile);
                var history = JsonConvert.DeserializeObject<Dictionary<string, DownloadStats>>(json);
                if (history != null)
                    foreach (var item in history)
                        DownloadHistory[item.Key] = item.Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cargando historial: {ex}");
            }
        }
    }

    private static void SaveHistory()
    {
        try
        {
            var json = JsonConvert.SerializeObject(DownloadHistory, Formatting.Indented);
            File.WriteAllText(HistoryFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error guardando historial: {ex}");
        }
    }
}

public class DownloadRequest
{
    public ulong UserId { get; set; }
    public string Url { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string Format { get; set; } = "";
    public string Quality { get; set; } = "";
    public ISocketMessageChannel Channel { get; set; } = null!;
    public DateTime RequestedAt { get; set; }
}

public class DownloadStats
{
    public string Title { get; set; } = "";
    public ulong UserId { get; set; }
    public DateTime Date { get; set; }
    public int Count { get; set; } = 1;
}
