// demo script — cycles the `temperature` variable every 500ms
var t = plc.readVariable('temperature') + 0.5;
if (t > 37.5) { t = 20.0; }
plc.writeVariable('temperature', t);
console.log('temperature =', t);
// phase 8 types: struct leaves and a dt timestamp
plc.writeVariable('motor.speed', Math.round(t * 10));
plc.writeVariable('motor.on', t > 30);
plc.writeVariable('motor.label', t > 30 ? 'hot' : 'cold');
plc.writeVariable('updated', new Date());
