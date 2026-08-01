@echo off
rem ---------------------------------------------------------------------------
rem Test double for the headless Claude CLI. Stands in for `claude -p --output-format json`
rem so the retry path can be exercised — and demonstrated — without spending a cent or
rem waiting for a real model to misbehave on cue.
rem
rem   set ClaudeCli__Path=%CD%\scripts\stub-claude-cli.cmd
rem   set DataDirectory=%TEMP%\docextract-stub
rem   dotnet run --project DocExtract\DocExtract.csproj -- extract <any-path>
rem
rem First invocation answers in prose (an unparseable payload); later invocations return a
rem well-formed envelope. So one document goes: attempt 1 fails, attempt 2 succeeds, and the
rem artifact records attempts=2 with two cost lines in the ledger.
rem
rem   STUB_ALWAYS_FAIL=1   never return valid JSON — exercises the exhausted-retry path
rem                        (both attempts unparseable, document lands in needs-review)
rem
rem Delete %TEMP%\docextract-stub-attempt1.flag to arm the first-failure behaviour again.
rem ---------------------------------------------------------------------------
setlocal
set FLAG=%TEMP%\docextract-stub-attempt1.flag

if "%STUB_ALWAYS_FAIL%"=="1" goto prose
if exist "%FLAG%" goto json
echo armed> "%FLAG%"

:prose
echo {"is_error":false,"result":"Sure! I read the receipt - the vendor is Stub Cafe and the total is 12.34 EUR.","total_cost_usd":0.0019}
exit /b 0

:json
echo {"is_error":false,"result":"{\"vendor\":{\"value\":\"Stub Cafe\",\"confidence\":0.93},\"date\":{\"value\":\"2026-07-11\",\"confidence\":0.95},\"address\":{\"value\":\"12 Test Street\",\"confidence\":0.88},\"total\":{\"value\":12.34,\"confidence\":0.97},\"currency\":{\"value\":\"EUR\",\"confidence\":0.9},\"tax\":{\"value\":2.14,\"confidence\":0.8},\"line_items\":[]}","total_cost_usd":0.0021}
exit /b 0
