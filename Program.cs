using System;
using System.Threading;
using System.Threading.Tasks;
using FashionBot.Handlers;
using FashionBot.Services;
using FashionBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Telegram.Bot;
using Telegram.Bot.Polling;

using Telegram.Bot.Types.Enums;
class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    config.AddEnvironmentVariables();
                    if (hostingContext.HostingEnvironment.IsDevelopment())
                    {
                        config.AddUserSecrets<Program>();
                    }
                })
                .ConfigureServices((context, services) =>
                {
                    // 1. Получаем и проверяем конфигурацию
                    var configuration = context.Configuration;
                    
                    // 2. Проверяем обязательные настройки ДО регистрации сервисов
                    var dbSettings = configuration.GetSection("DatabaseSettings").Get<DatabaseSettings>();
                    if (string.IsNullOrWhiteSpace(dbSettings?.ConnectionString))
                        throw new InvalidOperationException("Database connection string is not configured");

                    var openAiSettings = configuration.GetSection("OpenAISettings").Get<OpenAISettings>();
                    if (string.IsNullOrWhiteSpace(openAiSettings?.BaseUrl))
                        throw new InvalidOperationException("OpenAI BaseUrl is not configured");
                    if (string.IsNullOrWhiteSpace(openAiSettings.ApiKey))
                        throw new InvalidOperationException("OpenAI ApiKey is not configured");

                    // 3. Регистрируем проверенные настройки
                    services.Configure<AppSettings>(configuration);
                    
                    // 4. Регистрируем сервисы с явной проверкой зависимостей
                    services.AddSingleton<IDatabaseService>(provider => 
                        new DatabaseService(
                            Options.Create(dbSettings),
                            provider.GetRequiredService<ILogger<DatabaseService>>()));

                    services.AddSingleton<IOpenAIService>(provider => 
                        new OpenAIService(
                            Options.Create(openAiSettings),
                            provider.GetRequiredService<ILogger<OpenAIService>>()));

                    services.AddSingleton<IRequestQueueService, RequestQueueService>();
                    
                    services.AddSingleton<TelegramBotHandler>();

                    services.AddHttpClient("telegram_bot_client")
                        .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
                        {
                            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
                            if (string.IsNullOrWhiteSpace(settings.TelegramBotSettings.BotToken))
                                throw new InvalidOperationException("Telegram bot token is not configured");
                            return new TelegramBotClient(settings.TelegramBotSettings.BotToken, httpClient);
                        });

                    services.AddHostedService<BotBackgroundService>();
                })
                .Build();

            // Инициализация базы данных
            var dbService = host.Services.GetRequiredService<IDatabaseService>();
            await dbService.InitializeDatabaseAsync();

            Console.WriteLine("Starting bot...");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Application startup failed: {ex.Message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }
}