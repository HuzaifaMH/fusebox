-- Replaces a reservation with the real cost of the call. Idempotent: the hold is deleted
-- as it is read, so a second settlement finds nothing and changes nothing.
--
-- Parameters
--   budgetKey  key    the window's spend counter the reservation was placed in
--   holdKey    key    the reservation's recorded amount
--   actual     value  the real cost, in micro-dollars
--
-- Returns { applied, spent }
--   applied  1 if the reservation was settled, 0 if it was unknown, settled already, or expired
--   spent    the window's spend afterwards, or 0 when nothing was applied

local held = redis.call('GET', @holdKey)

if not held then
  return { 0, 0 }
end

redis.call('DEL', @holdKey)

local spent = 0

-- If the window's counter has expired there is nothing left to adjust. Calling INCRBY on a
-- missing key would recreate it without an expiry, so the counter is only touched while it exists.
if redis.call('EXISTS', @budgetKey) == 1 then
  spent = redis.call('INCRBY', @budgetKey, tonumber(@actual) - tonumber(held))

  if spent < 0 then
    redis.call('SET', @budgetKey, '0', 'KEEPTTL')
    spent = 0
  end
end

return { 1, spent }
