# Журнал изменений - PilotServerDebug

Формат: раздел `## [X.Y.Z] - дата` на каждую версию. Текст раздела становится описанием GitHub Release.

## [1.1.0] - 2026-11-20

- Утилита вынесена из монорепозитория PilotProAdmin (`server-extensions/Debug`) в отдельный репозиторий PilotOpenTools/PilotServerDebug. Код и формат `debug.json` не менялись.
- Релизы собираются GitHub Actions: `PilotServerDebug-<версия>.zip` и `SHA256SUMS.txt`.
- `dotnet publish` больше не копирует `debug.zip` в папку веб-админки: ссылка на скачивание ведёт на релизы GitHub.
