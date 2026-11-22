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


## Публичный NUnit-проект — подтверждённые P2/P3 границы (draft #85, 22.11.2026)

Оригинальный `desh2001/Selenium-NUnit-Automation@edacb9feb41a7e21d9b6bf6924f24c11698a4427`
(.NET 10) на Linux и Windows **собирается**; NUnit находит **10 тестов**.
Оригинальные тесты против внешнего сайта **не выполнялись**. Настоящий
`Core generate --build` создаёт отдельный Playwright/NUnit проект:
четыре файла C#, `.csproj`, manifest, успешная сборка и обнаружение
**10/10 тестов**. Сами тесты target **не запускались**. C# после
CRLF→LF идентичен на Linux/Windows, но в нём остаются обязательные
`TODO` на DriverManager, POM, действия, assertions и lifecycle.
**Компиляция ≠ поведенческая эквивалентность.**

Полный исходный POM-граф: 15 методов, из них 14
`requires_review`, а `AlertsPage.WaitForAlert` — `blocked`
(неподдержанный `IAlert`). Отдельно исходные публичные методы
SampleApp выполнены в реальном Chromium через Selenium и
Core-generated Playwright на localhost: одинаковые успешный/ошибочный
логины, семь NUnit TRX и три отрицательные мутации. Это **не** запуск
оригинального полного TestFixture/DriverManager.

`acceptance-inspect <original-project-root> <AnalysisId> <CandidateId>`
использует идентификаторы из точного `plan`, выводит
source-target lineage, обнаруживает **125 обязательств / 115
неперенесённых** и возвращает `rejected_incomplete` на обеих ОС.
Старый CandidateId отвергается, статус не становится PASS от того,
что собрался проект. Анализ только read-only: никакого выполнения кода.

См. [сводку приёмки](docs/P2-P3-PUBLIC-SAMPLEAPP-PROJECT-GATE.md),
[Core draft #85](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/85)
и [RC #80](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/80).
`Selenio` и `re-motion/Framework` пока подтверждены только по
исходникам. Поведенческая приёмка всех трёх проектов **UNKNOWN**,
P1 #59 — OPEN / NO FIX CLAIM. Релиз, merge и autoapply запрещены.
