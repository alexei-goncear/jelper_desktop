# Мини-приложение для Replicate

Этот скрипт показывает, как вызвать модель `recraft-ai/recraft-crisp-upscale` из сервиса [Replicate](https://replicate.com/) и сохранить результат локально.

## Требования
- Python 3.10+
- Аккаунт в Replicate и персональный токен доступа (`REPLICATE_API_TOKEN`).

## Настройка окружения
```bash
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

## Запуск
1. Передайте токен либо переменной окружения, либо аргументом `--token`:
   - macOS/Linux: `export REPLICATE_API_TOKEN="r8_..."`
   - PowerShell: `$env:REPLICATE_API_TOKEN="r8_..."`
   - CLI аргумент: `--token r8_...`
2. Выполните скрипт, указав URL/путь исходного изображения и имя файла для результата:
   ```bash
   python recraft_cli.py --image "https://replicate.delivery/pbxt/MKdkS3Po0PXytPbTXh4bOlBX1BZRuXH4o34yXVEakeBlpiTW/blonde_mj.png" --output output.webp --token r8_...
   ```
3. После успешного завершения команда вернёт URL сгенерированного файла на CDN Replicate и путь до сохранённого локального файла.

## Полезно знать
- В `--image/--image-url` можно передать либо публичный URL, либо путь к локальному файлу (например `C:\Users\me\image.png`).
- Вы можете передать любой путь в `--output`, например `artifacts/upscaled.webp`; папки будут созданы автоматически.
- Повторный запуск с тем же путём перезапишет файл.
