# Selenium → Playwright Migrator Core — руководство (подготовка RC)

**Статус: предварительная версия, выпуск в production запрещён.** Мигратор выполняет консервативный анализ Selenium C# NUnit, создаёт предложения для ревью и поддерживает отдельную, явно разрешённую генерацию/проверку. Windows Chromium orphan в [issue #59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59) подтверждён реальным native-LIVE FAIL, но исправление продукта **не доказано**.

## 1. Требования

Нужны .NET SDK 10 (`global.json`), Windows либо Linux, доверенный локальный NUnit-проект на Selenium C# и явные адаптеры для специфичных Page Object. Уже Playwright-проекты, xUnit, неоднозначные NUnit fixtures, неизвестные селекторы и побочные эффекты не должны молча считаться успешно перенесёнными.

## 2. Анализ без изменения исходников

```bash
dotnet restore Migrator.AssemblyCli/Migrator.AssemblyCli.csproj
dotnet build Migrator.AssemblyCli/Migrator.AssemblyCli.csproj -c Release
dotnet run --project Migrator.AssemblyCli -c Release -- plan /absolute/path/to/selenium-project
dotnet run --project Migrator.AssemblyCli -c Release -- pom-inspect /absolute/path/to/POM-project
dotnet run --project Migrator.AssemblyCli -c Release -- pom-plan /absolute/path/to/POM-project Namespace.PageObject MethodName EXACT_SOURCE_TREE_HASH
```

Проверяйте scope, `SourceTreeHash`, пропущенные действия, lifecycle и статус `requires_review` или `blocked`. `--emit-target` — отдельное локальное разрешение на вывод C# с селекторами. В публичные логи этот вывод помещать нельзя.

## 3. Создание отдельного результата

Только после проверки adapter-config создайте **новую** папку результата (существующую мигратор не перезапишет):

```bash
dotnet run --project Migrator.AssemblyCli -c Release -- generate /absolute/source-project /absolute/new-target --config /absolute/adapter-config.json
dotnet run --project Migrator.AssemblyCli -c Release -- verify /absolute/source-project /absolute/new-target --config /absolute/adapter-config.json
```

Параметры `--build` и `--test` выполняют сборку/тесты и разрешены только для доверенного либо изолированного проекта. Зелёный `dotnet test` не означает эквивалентность Selenium и Playwright.

## 4. Строгая приёмка

- `rejected_incomplete`: потеряны исходные action, assertion, `SetUpFixture`, унаследованный NUnit lifecycle, helper, аргумент или путь отказа. Остановка.
- `blocked_unverified` и `candidate_unverified`: есть синтаксический кандидат, но эквивалентность **не доказана**.
- `syntax`, `compile`, `testDiscovery`, `runner`, `realBrowser`, `behavioralEquivalence` — независимые стадии. Не выполнявшаяся проверка имеет `not_run`.
- После любой правки целевого файла меняются `CandidateId` и `TargetTreeHash`; старое evidence недействительно.
- Для подтверждения реального проекта нужны независимые Selenium и Playwright наблюдения: DOM, порядок действий, побочные эффекты, ошибки и NUnit lifecycle, негативные мутации и явное разрешение выполнения.

В CI сейчас есть восемь положительных браузерных сценариев **синтетического локального стенда** и два отрицательных, включая ложнозелёный NUnit PASS без Click. Три публичных сторонних проекта исследуются **только read-only** — поведенческий результат их миграции `UNKNOWN`. Приватный ArBilling не используется.

## 5. Условия релиза

См. [release gates](docs/RC-READINESS.md). До доказанного устранения P1 #59, полной матрицы 28 Core + 2 MCP и независимой поведенческой приёмки **NO-GO**. Исходный Windows P47, реальный Node SIGTERM, 12-секундный gate, PID/FILETIME, fail-fast и исторические ZIP остаются неизменными. Внешний Win32 Job Object не заменяет исправление продукта.

Не снижайте требования после FAIL, не скрывайте `UNKNOWN` и не выполняйте чужой код без разрешения.
