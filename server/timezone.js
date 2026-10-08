/** Calendar-only manual logs use local noon, avoiding DST transitions around midnight. */
export function resolveTimezone(value = 'Europe/Moscow') {
  if (typeof value !== 'string' || value.length > 100 || /^[+-]/.test(value)) throw new RangeError('Invalid timezone');
  return new Intl.DateTimeFormat('en-US', { timeZone: value }).resolvedOptions().timeZone;
}

function partsAt(instant, timeZone) {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).formatToParts(instant);
  return Object.fromEntries(parts.filter((part) => part.type !== 'literal').map((part) => [part.type, part.value]));
}

export function dateInTimezone(instant, timeZone) {
  const { year, month, day } = partsAt(instant, timeZone);
  return `${year.padStart(4, '0')}-${month}-${day}`;
}

export function localNoonInstant(date, timeZone) {
  const desired = Date.parse(`${date}T12:00:00.000Z`);
  let instant = desired;
  for (let attempt = 0; attempt < 4; attempt++) {
    const { year, month, day, hour, minute, second } = partsAt(new Date(instant), timeZone);
    const represented = Date.parse(`${year.padStart(4, '0')}-${month}-${day}T${hour}:${minute}:${second}.000Z`);
    const correction = desired - represented;
    instant += correction;
    if (!correction) break;
  }
  const result = new Date(instant);
  if (dateInTimezone(result, timeZone) !== date) throw new RangeError('Date does not exist in this timezone');
  return result.toISOString();
}
