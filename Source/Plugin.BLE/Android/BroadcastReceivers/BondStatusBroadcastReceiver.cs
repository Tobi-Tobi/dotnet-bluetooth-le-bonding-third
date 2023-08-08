using System;
using Android.Bluetooth;
using Android.Content;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Extensions;

namespace Plugin.BLE.BroadcastReceivers
{
    public class BondStatusBroadcastReceiver : BroadcastReceiver
    {
	    public event EventHandler<DeviceBondStateChangedEventArgs> BondStateChanged;

	    public override void OnReceive(Context context, Intent intent)
        {
            if (BondStateChanged == null)
            {
	            return;
            }

            var extraBondState = (Bond)intent.GetIntExtra(BluetoothDevice.ExtraBondState, (int)Bond.None);
            var bluetoothDevice = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
            
            var address = bluetoothDevice?.Address;
            var bondState = extraBondState.FromNative();
            
            BondStateChanged?.Invoke(this, new DeviceBondStateChangedEventArgs { Address = address, State = bondState });
        }
    }
}
