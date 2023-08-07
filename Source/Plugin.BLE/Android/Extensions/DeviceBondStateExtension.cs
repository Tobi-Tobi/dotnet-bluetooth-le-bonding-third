using Android.Bluetooth;
using Plugin.BLE.Abstractions;

namespace Plugin.BLE.Extensions
{
	internal static class DeviceBondStateExtension
	{
		public static DeviceBondState FromNative(this Bond bondState)
		{
			return bondState switch
			{
				Bond.None => DeviceBondState.NotBonded,
				Bond.Bonding => DeviceBondState.Bonding,
				Bond.Bonded => DeviceBondState.Bonded,
				_ => DeviceBondState.NotSupported
			};
		}

	}
}
