local core, effects, optional = assert(arg[1]), assert(arg[2]), assert(arg[3])
local sep = package.config:sub(1, 1)
local function load(root, name)
    local env = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(root .. sep .. name .. '.script'))
    setfenv(chunk, env); chunk(); _G[name] = env; return env
end
anomaly_donation_bridge_common = load(core, 'anomaly_donation_bridge_common')
anomaly_donation_bridge_registry = load(core, 'anomaly_donation_bridge_registry')
load(optional, 'anomaly_donation_bridge_effect_hardcoon_adb_pda_message').on_game_start()
local handler = assert(anomaly_donation_bridge_registry.get('hardcoon.adb.pda_message'))
local common = anomaly_donation_bridge_common
local function encoded(text) return common.game_text_cp1251_lossy(text, '?') end
local calls = {}
-- A message-only effect must not access actor properties or change the world.
local actor = setmetatable({}, {
    __index = function() error('unexpected actor access') end,
    __newindex = function() error('unexpected actor mutation') end
})
news_manager = { send_tip = function(target, text, icon, sender, duration)
    assert(rawequal(target, actor) and icon == nil and sender == nil and duration == 6000)
    calls[#calls + 1] = text
end }
assert(handler.is_supported({ actor = actor }))
local function send(donor, message)
    local context = { actor = actor }
    assert(handler.can_execute(context, { donor_name = donor, donation_message = message }))
    local status, reason = handler.execute(context)
    assert(status == 'executed' and reason == 'pda_message_sent')
    return calls[#calls]
end
assert(send('  Сталкер\nиз чата  ', '  Привет\nиз доната!  ') ==
    '%c[255,238,153,26]' .. encoded('Сталкер из чата') .. '%c[default]: ' .. encoded('Привет из доната!'))
assert(send('Следующий эффект', 'Ничего не делать') ==
    '%c[255,238,153,26]' .. encoded('Следующий эффект') .. '%c[default]: ' .. encoded('Ничего не делать'))
assert(send('Игрок', '') == '%c[255,238,153,26]' .. encoded('Игрок') .. '%c[default]: ')
local literal = send('%c[255,0,0,0]Игрок', 'Привет %c[default]😀');
assert(literal == '%c[255,238,153,26]' .. encoded('Игрок') .. '%c[default]: ' .. encoded('Привет 😀'))
local valid, status, reason = handler.can_execute({ actor = nil }, { donor_name = 'Игрок' })
assert(not valid and status == 'deferred' and reason == 'actor_unavailable')
valid, status, reason = handler.can_execute({ actor = actor }, { script = 'anything' })
assert(not valid and status == 'rejected' and reason == 'parameter_not_supported')
news_manager = nil
assert(not handler.is_supported({ actor = actor }))
valid, status, reason = handler.can_execute({ actor = actor }, { donor_name = 'Игрок' })
assert(not valid and status == 'rejected' and reason == 'pda_message_api_unavailable')
news_manager = { send_tip = function() error('UI failed') end }
local context = { actor = actor }
assert(handler.can_execute(context, { donor_name = 'Игрок', donation_message = 'Текст' }))
status, reason = handler.execute(context)
assert(status == 'uncertain' and reason == 'pda_message_send_failed')
assert(#calls == 4 and handler.update == nil and handler.save == nil and handler.load == nil)
print('PDA message: donor/body, voting, plain text, no actor mutations and safe API failure passed')
