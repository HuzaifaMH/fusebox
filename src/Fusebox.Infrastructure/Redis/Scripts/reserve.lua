-- Atomically reserves part of a tenant's budget for the current window.
--
-- Parameters
--   budgetKey  key    the window's spend counter, in micro-dollars
--   holdKey    key    where this reservation's amount is recorded, so it can be settled once
--   amount     value  the worst-case cost to hold
--   limit      value  the tenant's budget for the window
--   ttlMs      value  how long the counter and hold live, in milliseconds
--
-- Returns { granted, spent }
--   granted  1 if the amount fit and was held, 0 if not
--   spent    the window's spend after the hold if granted, or the spend that left no room if not

local spent = tonumber(redis.call('GET', @budgetKey) or '0')
local amount = tonumber(@amount)

if spent + amount > tonumber(@limit) then
  return { 0, spent }
end

local newSpent = redis.call('INCRBY', @budgetKey, amount)
redis.call('PEXPIRE', @budgetKey, tonumber(@ttlMs))
redis.call('SET', @holdKey, tostring(amount), 'PX', tonumber(@ttlMs))

return { 1, newSpent }
