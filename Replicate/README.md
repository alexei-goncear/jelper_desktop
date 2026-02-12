# Мини-приложение для Replicate

`recraft_cli.py` — это thin-wrapper вокруг модели `recraft-ai/recraft-crisp-upscale` в [Replicate](https://replicate.com/). Скрипт принимает параметры из .NET-клиента, скачивает результат и выводит JSON с итогом.

## Требования
- Python 3.10+
- Установленные зависимости `pip install -r requirements.txt`
- Переменная окружения `REPLICATE_API_TOKEN`

## Настройка окружения
```bash
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

## Запуск
```bash
export REPLICATE_API_TOKEN="r8_..."  # Windows PowerShell: $env:REPLICATE_API_TOKEN="r8_..."
python recraft_cli.py --image "~/Pictures/source.png" --output "~/Pictures/source.upscaled.png" --mode upscale16mp
```

После успешного выполнения в STDOUT попадёт JSON вида:

```json
{"status":"ok","output_path":"/abs/path/source.upscaled.png","remote_url":"https://replicate.delivery/...","message":"mode=upscale16mp"}
```

## Полезно знать
- В `--image` можно передать либо публичный URL, либо путь к локальному файлу.
- Путь из `--output` создаётся автоматически (папки будут вложены).
- В случае ошибки информация уходит в STDERR и процесс завершается с кодом `1`.
