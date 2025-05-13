

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FashionBot.Models;
using FashionBot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace FashionBot.Handlers
{

public class TelegramBotHandler
{
    private readonly TelegramBotClient _botClient;
    private readonly IOpenAIService _openAIService;
    private readonly IRequestQueueService _queueService;
    private readonly IDatabaseService _databaseService;
    private readonly ILogger<TelegramBotHandler> _logger;
    private readonly AppSettings _appSettings;
    
    private readonly Dictionary<long, UserState> _userStates = new();

    public TelegramBotHandler(
        IOptions<AppSettings> appSettings,
        IOpenAIService openAIService,
        IRequestQueueService queueService,
        IDatabaseService databaseService,
        ILogger<TelegramBotHandler> logger)
    {
        _appSettings = appSettings?.Value ?? throw new ArgumentNullException(nameof(appSettings));
        _openAIService = openAIService ?? throw new ArgumentNullException(nameof(openAIService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_appSettings.TelegramBotSettings?.BotToken))
        {
            throw new ArgumentException("Bot token is not configured");
        }

        _botClient = new TelegramBotClient(_appSettings.TelegramBotSettings.BotToken);
        _logger.LogInformation("Telegram bot client initialized");
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Message is not { } message)
                return;

            var chatId = message.Chat.Id;
            var userId = message.From.Id;

            if (!_userStates.TryGetValue(userId, out var userState))
            {
                userState = new UserState
                {
                    UserId = userId,
                    CurrentState = UserStateState.Idle
                };
                _userStates[userId] = userState;
            }

            if (message.Text is { } messageText)
            {
                if (messageText.StartsWith("/start"))
                {
                    await ShowMainMenu(chatId, cancellationToken);
                    return;
                }

                if (messageText.StartsWith("/settings") && userId == _appSettings.TelegramBotSettings.AdminUserId)
                {
                    await HandleSettingsCommand(chatId, messageText, cancellationToken);
                    return;
                }

                switch (userState.CurrentState)
                {
                    case UserStateState.WaitingForOutfitPrompt:
                        userState.Prompt = messageText;
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Теперь отправьте фотографии одежды, которую хотите объединить в образ.",
                            cancellationToken: cancellationToken);
                        userState.CurrentState = UserStateState.WaitingForOutfitImages;
                        return;

                    case UserStateState.WaitingForMatchingPrompt:
                        userState.Prompt = messageText;
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Теперь отправьте фотографию одежды, для которой нужно подобрать образ.",
                            cancellationToken: cancellationToken);
                        userState.CurrentState = UserStateState.WaitingForMatchingImage;
                        return;
                }
            }

            if (message.Photo is { } photos)
            {
                var photo = photos.Last();
                var fileId = photo.FileId;
                var fileInfo = await _botClient.GetFileAsync(fileId, cancellationToken);
                var fileUrl = $"https://api.telegram.org/file/bot{_appSettings.TelegramBotSettings.BotToken}/{fileInfo.FilePath}";

                switch (userState.CurrentState)
                {
                    case UserStateState.WaitingForOutfitImages:
                        userState.Images.Add(fileUrl);
                        await _botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Фотография получена. Отправьте еще или нажмите /generate для создания образа.",
                            cancellationToken: cancellationToken);
                        return;

                    case UserStateState.WaitingForMatchingImage:
                        userState.Images.Add(fileUrl);
                        await ProcessMatchingRequest(userState, chatId, cancellationToken);
                        return;
                }
            }

            if (message.Text == "Объединить вещи в образ")
            {
                userState.CurrentState = UserStateState.WaitingForOutfitPrompt;
                userState.RequestType = RequestType.CombineOutfit;
                userState.Images.Clear();
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Введите описание или пожелания для образа (например, 'деловой стиль' или 'повседневный образ'):",
                    cancellationToken: cancellationToken);
                return;
            }

            if (message.Text == "Подобрать образ к вещи")
            {
                userState.CurrentState = UserStateState.WaitingForMatchingPrompt;
                userState.RequestType = RequestType.MatchOutfit;
                userState.Images.Clear();
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Введите описание или пожелания для подбираемого образа (например, 'дополнить джинсами' или 'подобрать верх'):",
                    cancellationToken: cancellationToken);
                return;
            }

            if (message.Text == "/generate" && userState.CurrentState == UserStateState.WaitingForOutfitImages)
            {
                if (userState.Images.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Сначала отправьте фотографии одежды.",
                        cancellationToken: cancellationToken);
                    return;
                }

                await ProcessOutfitRequest(userState, chatId, cancellationToken);
                return;
            }

            await ShowMainMenu(chatId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update");
        }
    }

    private async Task ShowMainMenu(long chatId, CancellationToken cancellationToken)
    {
        var replyKeyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "Объединить вещи в образ" },
            new KeyboardButton[] { "Подобрать образ к вещи" }
        })
        {
            ResizeKeyboard = true
        };

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Выберите режим работы:",
            replyMarkup: replyKeyboard,
            cancellationToken: cancellationToken);
    }

    private async Task ProcessOutfitRequest(UserState userState, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            if (userState?.Images == null || !userState.Images.Any())
            {
                await _botClient.SendTextMessageAsync(chatId, "Нет изображений для обработки", cancellationToken: cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(userState.Prompt))
            {
                await _botClient.SendTextMessageAsync(chatId, "Не указано описание образа", cancellationToken: cancellationToken);
                return;
            }

            var request = new FashionRequest
            {
                Id = Guid.NewGuid(),
                UserId = userState.UserId,
                RequestType = FashionRequestType.CombineOutfit,
                Images = new List<string>(userState.Images),
                Prompt = userState.Prompt,
                Status = FashionRequestStatus.Queued,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                await _databaseService.SaveRequestAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сохранения запроса");
                await _botClient.SendTextMessageAsync(chatId, "Ошибка обработки запроса", cancellationToken: cancellationToken);
                return;
            }

            await _botClient.SendTextMessageAsync(chatId, "Создаю образ...", cancellationToken: cancellationToken);

            await _queueService.EnqueueRequestAsync(async () =>
            {
                try
                {
                    var resultUrl = await _openAIService.GenerateImageFromClothesAsync(
                        request.Images,
                        request.Prompt,
                        _appSettings?.Prompts?.OutfitGenerationSystemPrompt ?? string.Empty,
                        _appSettings?.Prompts?.OutfitGenerationUserPrompt ?? string.Empty);

                    if (string.IsNullOrEmpty(resultUrl))
                        throw new Exception("Не удалось сгенерировать изображение");

                    request.ResultUrl = resultUrl;
                    request.Status = FashionRequestStatus.Completed;
                    request.ProcessedAt = DateTime.UtcNow;

                    await _databaseService.SaveRequestAsync(request);

                    await _botClient.SendPhotoAsync(
                        chatId: chatId,
                        photo: resultUrl,
                        caption: "Ваш образ готов!",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка обработки запроса");
                    request.Status = FashionRequestStatus.Failed;
                    await _databaseService.SaveRequestAsync(request);
                    
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Ошибка при создании образа",
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    _userStates.TryGetValue(userState.UserId, out var state);
                    if (state != null)
                        state.CurrentState = UserStateState.Idle;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанная ошибка в ProcessOutfitRequest");
            await _botClient.SendTextMessageAsync(chatId, "Произошла ошибка", cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessMatchingRequest(UserState userState, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            if (userState?.Images == null || userState.Images.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Необходимо отправить изображение", cancellationToken: cancellationToken);
                return;
            }

            var request = new FashionRequest
            {
                Id = Guid.NewGuid(),
                UserId = userState.UserId,
                RequestType = FashionRequestType.MatchOutfit,
                Images = new List<string> { userState.Images.First() },
                Prompt = userState.Prompt ?? string.Empty,
                Status = FashionRequestStatus.Queued,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                await _databaseService.SaveRequestAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сохранения запроса");
                await _botClient.SendTextMessageAsync(chatId, "Ошибка обработки запроса", cancellationToken: cancellationToken);
                return;
            }

            await _botClient.SendTextMessageAsync(chatId, "Подбираю образ...", cancellationToken: cancellationToken);

            await _queueService.EnqueueRequestAsync(async () =>
            {
                try
                {
                    var resultUrl = await _openAIService.GenerateMatchingOutfitAsync(
                        request.Images.First(),
                        request.Prompt,
                        _appSettings?.Prompts?.MatchingItemsSystemPrompt ?? string.Empty,
                        _appSettings?.Prompts?.MatchingItemsUserPrompt ?? string.Empty);

                    if (string.IsNullOrEmpty(resultUrl))
                        throw new Exception("Не удалось подобрать образ");

                    request.ResultUrl = resultUrl;
                    request.Status = FashionRequestStatus.Completed;
                    request.ProcessedAt = DateTime.UtcNow;

                    await _databaseService.SaveRequestAsync(request);

                    await _botClient.SendPhotoAsync(
                        chatId: chatId,
                        photo: resultUrl,
                        caption: "Вот подобранный образ!",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка обработки запроса");
                    request.Status = FashionRequestStatus.Failed;
                    await _databaseService.SaveRequestAsync(request);
                    
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Ошибка при подборе образа",
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    _userStates.TryGetValue(userState.UserId, out var state);
                    if (state != null)
                        state.CurrentState = UserStateState.Idle;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанная ошибка в ProcessMatchingRequest");
            await _botClient.SendTextMessageAsync(chatId, "Произошла ошибка", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleSettingsCommand(long chatId, string messageText, CancellationToken cancellationToken)
    {
        var parts = messageText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Используйте формат:\n/settings\n[параметр]=[значение]\n[параметр]=[значение]...",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var settings = await _databaseService.GetAppSettingsAsync() ?? _appSettings;
            var prompts = await _databaseService.GetPromptsAsync() ?? _appSettings.Prompts;

            for (int i = 1; i < parts.Length; i++)
            {
                var setting = parts[i].Split('=', 2);
                if (setting.Length != 2) continue;

                var key = setting[0].Trim().ToLower();
                var value = setting[1].Trim();

                if (key.StartsWith("openai_"))
                {
                    switch (key)
                    {
                        case "openai_max_concurrent_requests":
                            settings.OpenAISettings.MaxConcurrentRequests = int.Parse(value);
                            break;
                        case "openai_image_size":
                            settings.OpenAISettings.ImageSize = value;
                            break;
                        case "openai_image_quality":
                            settings.OpenAISettings.ImageQuality = value;
                            break;
                        case "openai_image_style":
                            settings.OpenAISettings.ImageStyle = value;
                            break;
                    }
                }
                else if (key.StartsWith("prompt_"))
                {
                    switch (key)
                    {
                        case "prompt_outfit_generation_system":
                            prompts.OutfitGenerationSystemPrompt = value;
                            break;
                        case "prompt_outfit_generation_user":
                            prompts.OutfitGenerationUserPrompt = value;
                            break;
                        case "prompt_matching_items_system":
                            prompts.MatchingItemsSystemPrompt = value;
                            break;
                        case "prompt_matching_items_user":
                            prompts.MatchingItemsUserPrompt = value;
                            break;
                    }
                }
            }

            await _databaseService.UpdateAppSettingsAsync(settings);
            await _databaseService.UpdatePromptsAsync(prompts);

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Настройки успешно обновлены",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating settings");
            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"Ошибка при обновлении настроек: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }
}
public class UserState
{
    public long UserId { get; set; }
    public UserStateState CurrentState { get; set; }
    public RequestType RequestType { get; set; }
    public List<string> Images { get; set; } = new();
    public string Prompt { get; set; } = string.Empty;
}

public enum UserStateState
{
    Idle,
    WaitingForOutfitPrompt,
    WaitingForOutfitImages,
    WaitingForMatchingPrompt,
    WaitingForMatchingImage
}

public enum RequestType
{
    CombineOutfit,
    MatchOutfit
}

public class BotBackgroundService : BackgroundService
{
    private readonly ILogger<BotBackgroundService> _logger;
    private readonly ITelegramBotClient _botClient;
    private readonly TelegramBotHandler _botHandler;
    private readonly IRequestQueueService _queueService;

    public BotBackgroundService(
        ILogger<BotBackgroundService> logger,
        ITelegramBotClient botClient,
        TelegramBotHandler botHandler,
        IRequestQueueService queueService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
        _botHandler = botHandler ?? throw new ArgumentNullException(nameof(botHandler));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _botClient.StartReceiving(
            updateHandler: _botHandler.HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                ThrowPendingUpdates = true,
                AllowedUpdates = Array.Empty<UpdateType>()
            },
            cancellationToken: stoppingToken);

        _logger.LogInformation("Bot started receiving updates");

        await _queueService.ProcessQueueAsync(stoppingToken);
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram polling error");
        return Task.CompletedTask;
    }
}
}