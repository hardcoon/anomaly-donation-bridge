# Extension API v1

API предназначен для локальных модпаков, которые собираются вместе с собственной
версией приложения. Он не загружает код из сети и не исполняет текст доната как Lua.

Описание карточки теперь находится в начале того же Lua-файла. Приложение
читает только JSON-заголовок, не выполняя Lua-код:

```lua
--[[ADB_PRESET_V1
{
  "schemaVersion": 1,
  "handlerId": "author.modpack.weather",
  "displayName": "Погода",
  "description": "Меняет погоду в текущей локации.",
  "category": "world",
  "cooldownSeconds": 3,
  "parameterDefinitions": [
    {"key":"value","displayName":"Интенсивность","kind":"Integer",
     "minimum":1,"maximum":10,"defaultValue":"5"}
  ],
  "parameters": {"value":"5"}
}
ADB_PRESET_END]]
```

Сохраните файл под именем `anomaly_donation_bridge_effect_author_weather.script`
в `effects/gamedata/scripts` рядом с EXE и установите его через настройки
приложения в игру или включённый мод MO2. `handlerId` в заголовке обязан
совпадать с ID регистрации ниже. Карточка не появляется до установки
идентичного Lua-файла. Прежний отдельный JSON manifest не требуется.

Расширение размещает отдельный namespaced `.script` в `gamedata/scripts` и в своём
`on_game_start` регистрирует действие вида `author.modpack.weather` (не более
128 ASCII-символов). Требуется минимум три непустых сегмента через точку; каждый
сегмент начинается с `a-z`, содержит только `a-z0-9_-` и не длиннее 32 символов:

```lua
local function can_execute(context, params)
	for key in pairs(params) do
		if key ~= "value" then
			return false, "rejected", "parameter_not_supported"
		end
	end
	local value = tonumber(params.value)
	if value == nil or value < 1 or value > 10 then
		return false, "rejected", "value_invalid"
	end
	if context.actor == nil then
		return false, "deferred", "actor_unavailable"
	end
	context.vendor_value = value
	return true
end

local function execute(context)
	-- Здесь находится заранее установленная локальная логика мода.
	return "executed", "vendor_action_done_" .. tostring(context.vendor_value)
end

function on_game_start()
	local ok, reason = anomaly_donation_bridge_registry.register("author.modpack.weather", {
		can_execute = can_execute,
		execute = execute
	})
	if not ok then printf("Anomaly Donation Bridge extension rejected: %s", reason) end
end
```

`can_execute` обязан только проверять параметры и состояние, не меняя игровой мир.
`execute` возвращает один из статусов:

- `executed` — изменение выполнено и подтверждено;
- `rejected` — команда недопустима и повторять её не нужно;
- `deferred` — состояние временно небезопасно, приложение может повторить позже;
- `uncertain` — изменение началось, но итог или откат нельзя подтвердить.

Descriptor также может содержать безопасную проверку возможностей
`is_supported(context)` и необязательные локальные callbacks `update(now)`,
`on_game_load()` и `on_level_changing()`. Проверка не должна менять мир и должна
вернуть `true` либо `false, "reason"`. Зарегистрированный ID объявляется
приложению, чтобы неточная ранняя проба не скрывала кнопку; непосредственно перед
`can_execute` Lua повторяет `is_supported` уже с реальным контекстом и безопасно
отклоняет неподдерживаемую попытку. При формировании метаданных возможностей
аргумент `context` может быть `nil`, поэтому probe должен проверять наличие API
через безопасные глобальные модули, а не состояние конкретной команды.
`on_level_changing()` должен немедленно убрать временные эффекты расширения; во
время перехода новые команды не принимаются.
Все callbacks выполняются защищённо, но расширение само отвечает за откат своих
изменений и не должно считать ошибку callback успешным результатом.

Публичный `register` всегда требует namespace и не позволяет занять или заменить
встроенные ID — это верно при любом порядке `on_game_start`. Повторный ID также
отклоняется. За один запуск разрешено не более 64 extension ID; следующий
`register` возвращает `false, "extension_action_limit_reached"`. Вместе с лимитом
128 символов на ID это гарантирует, что единый `CAPABILITIES` протокола v1 остаётся
меньше 16 КиБ. Встроенные ID в этот лимит не входят и не могут быть вытеснены
расширениями. Встроенный `register_builtin` не является Extension API.

Протокол v1 уже допускает namespaced action ID, но приложение должно содержать
локальное описание пресета и схему параметров для нового действия в заголовке того
же файла. Регистрация Lua
сама по себе не расширяет безопасный динамический каталог, не снимает карантин и не
разрешает `loadstring`, `dofile`, произвольные команды консоли или remote eval.
