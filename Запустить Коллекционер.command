#!/bin/zsh
cd -- "${0:A:h}" || exit 1
export DOTNET_CLI_HOME="$PWD/.local/tooling"
export AVALONIA_TELEMETRY_OPTOUT=1
dotnet run --project src/Collector.Desktop/Collector.Desktop.csproj -- --data-dir "$PWD/.local-data"
if [[ $? != 0 ]]; then
  print 'Не удалось запустить приложение. Нажмите Enter, чтобы закрыть окно.'
  read
fi
