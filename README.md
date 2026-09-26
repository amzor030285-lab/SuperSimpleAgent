# SimpleLLMChat

Простой чат с LLM для Windows: консольная (CLI) и оконная (GUI) версии с набором инструментов — запуск команд, работа с файлами, скачивание, веб, память между сессиями. Поддерживает Windows XP и новее (цель сборки — .NET Framework 4.0 Client Profile), работает с любым OpenAI-совместимым API (OpenAI, локальные llama.cpp / Ollama и т.п.).

## Состав

| Папка | Что это |
|---|---|
| `SimpleLLMChat/` | Готовый релиз: скопировать папку и запустить `SimpleLLMChatGUI.exe` или `SimpleLLMChatCLI.exe` |
| `SimpleLLMChatCLI/` | Исходники консольной версии |
| `SimpleLLMChatGUI/` | Исходники оконной версии (WPF) |
| `Tools/` | Инструменты, которые могут вызывать модель: FileTools, ShellTools, WebTools, PythonTools, MemoryTools, SkillTools, DesktopTools |
| `Shared/` | Общий код (настройка, токены, защита ключей) |
| `Tests/` | Автоматические тесты всех компонентов |

## Запуск

1. Скопируйте папку `SimpleLLMChat` в любое место.
2. Запустите `SimpleLLMChatGUI.exe` (оконная) или `SimpleLLMChatCLI.exe` (консоль).
3. В настройках укажите endpoint и API-ключ любого OpenAI-совместимого сервера. Ключ хранится на диске зашифрованным (Windows DPAPI).

По умолчанию опасные инструменты (`run_shell_command`, `download_file`) выполняются только после подтверждения — режимы approval: `config` (подтверждать только перечисленные), `ask` (каждый вызов), `auto` (без вопросов). В CLI — флаг `--approval`, в REPL — команда `/approval`; в GUI — кнопка в левом нижнем углу.

## Сборка

- Требования: MSBuild из VS2019 Build Tools, .NET Framework 4.0 (Client Profile), C# 5.
- `build.bat` — сборка и упаковка релиза в `SimpleLLMChat/`.
- `build-and-test.bat [Suite]` — сборка продукта и тестов + прогон всех сьютов (или одного по имени).
- `run-tests.bat [Suite]` — только прогон уже собранных тестов.

Сьюты: FileTools, MemoryTools, SkillTools, ShellTools, PythonTools, WebTools, DesktopTools, SimpleLLMChatCLI (40 тестов), SimpleLLMChatGUI (16 тестов).

## Примечания

- Проверено на Windows XP SP3 (x86) и современных Windows.
- Контекст-компакция: при подходе к лимиту окна модель сначала суммаризирует старую часть диалога (сводка + последние 8k токенов дословно), только затем — ограниченное удаление, и в последнюю очередь сжатие одного самого большого сообщения.
- Запущенные инструментом процессы живут в Windows job object: отмена или таймаут убивает всё дерево, а не только прямой процесс.
