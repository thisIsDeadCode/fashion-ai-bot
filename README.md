# Fashion AI Assistant - Telegram Bot


## 📌 Описание проекта

Fashion AI Assistant - это интеллектуальный Telegram-бот, который помогает создавать модные образы с использованием искусственного интеллекта. Основные возможности:

- **Объединение вещей в образ** - создание гармоничного образа из нескольких предметов одежды
- **Подбор образа к вещи** - рекомендация комплектов для одного предмета одежды
- **Генерация новых вариантов** - создание визуализаций образов с помощью DALL-E 3

## 🛠 Технологический стек

| Компонент       | Технология               |
|----------------|-------------------------|
| Бэкенд         | .NET 7                  |
| База данных    | PostgreSQL 12+          |
| AI API         | OpenAI (DALL-E 3, GPT-4)|
| Telegram API   | Telegram.Bot            |
| Контейнеризация| Docker                  |

## ⚙️ Установка и настройка

### Предварительные требования

1. [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0)
2. [PostgreSQL 12+](https://www.postgresql.org/download/)
3. Аккаунт в [OpenAI](https://platform.openai.com/)
4. Telegram бот (создается через [@BotFather](https://t.me/BotFather))

### 1. Настройка базы данных

```sql
CREATE DATABASE fashion_ai;
CREATE USER fashion_ai_user WITH PASSWORD 'your_strong_password';
GRANT ALL PRIVILEGES ON DATABASE fashion_ai TO fashion_ai_user;
```
Запуск приложения 
```bash
# Восстановление зависимостей
dotnet restore

# Запуск в development режиме
dotnet run

# Публикация для production
dotnet publish -c Release -o ./publish
cd publish
dotnet FashionAI.dll
```

🌟 Особенности
Очередь запросов с ограничением скорости для работы с API OpenAI

Поддержка вертикальных изображений (9:16) для лучшего отображения в Telegram

Гибкая система промптов, настраиваемая через базу данных

Логирование всех операций

Поддержка административных функций

📈 Планы развития
Добавление примеров образов

Интеграция с облачным хранилищем для изображений

Поддержка нескольких языков

Система рейтинга образов

🤝 Участие в разработке
Мы приветствуем вклад в проект! Пожалуйста, создавайте issue для обсуждения новых функций и отправляйте pull requests.
