// demo script — cycles the `temperature` variable every 500ms
var t = plc.readVariable('temperature') + 0.5;
if (t > 37.5) { t = 20.0; }
plc.writeVariable('temperature', t);
console.log('temperature =', t);