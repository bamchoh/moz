// demo push: bump `setpoint` on plc-mirror every 2s; the [[client.push]] in
// moz.toml writes it to plc-a holding 20 (see `setpoint-received` there).
// Only write while the client link is up — otherwise the value would just
// wait in local memory until the connection comes back.
var status = plc.getVariableStatus('setpoint');
if (status === 'connected') {
  var v = (plc.readVariable('setpoint') + 1) % 100;
  plc.writeVariable('setpoint', v);
  console.log('setpoint =', v);
} else {
  console.log('setpoint link is', status);
}
