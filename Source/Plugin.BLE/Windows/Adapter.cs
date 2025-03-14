﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

#if WINDOWS_UWP
using Windows.System;
using Microsoft.Toolkit.Uwp.Connectivity;
#else
using Microsoft.UI.Dispatching;
using CommunityToolkit.WinUI.Connectivity;
#endif
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Extensions;
using System.Collections.Concurrent;

namespace Plugin.BLE.UWP
{
    public class Adapter : AdapterBase
    {
        private BluetoothLEHelper _bluetoothHelper;
        private BluetoothLEAdvertisementWatcher _bleWatcher;
        private DispatcherQueue _dq;

        /// <summary>
        /// Registry used to store device instances for pending operations : disconnect
        /// Helps to detect connection lost events.
        /// </summary>
        private readonly IDictionary<string, IDevice> _deviceOperationRegistry = new ConcurrentDictionary<string, IDevice>();

        public Adapter(BluetoothLEHelper bluetoothHelper)
        {
            _bluetoothHelper = bluetoothHelper;
            _dq = DispatcherQueue.GetForCurrentThread();
        }

        protected override Task StartScanningForDevicesNativeAsync(ScanFilterOptions scanFilterOptions, bool allowDuplicatesKey, CancellationToken scanCancellationToken)
        {
            var serviceUuids = scanFilterOptions?.ServiceUuids;
            var hasFilter = serviceUuids?.Any() ?? false;

            _bleWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = ScanMode.ToNative(), AllowExtendedAdvertisements = true };

            Trace.Message("Starting a scan for devices.");
            if (hasFilter)
            {
                //adds filter to native scanner if serviceUuids are specified
                foreach (var uuid in serviceUuids)
                {
                    _bleWatcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(uuid);
                }

                Trace.Message($"ScanFilters: {string.Join(", ", serviceUuids)}");
            }

            _bleWatcher.Received -= DeviceFoundAsync;
            _bleWatcher.Received += DeviceFoundAsync;

            _bleWatcher.Start();
            return Task.FromResult(true);
        }

        protected override void StopScanNative()
        {
            if (_bleWatcher != null)
            {
                Trace.Message("Stopping the scan for devices");
                _bleWatcher.Stop();
                _bleWatcher = null;
            }
        }

        protected override async Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters, CancellationToken cancellationToken)
        {
            Trace.Message($"Connecting to device with ID:  {device.Id.ToString()}");

            if (!(device.NativeDevice is ObservableBluetoothLEDevice nativeDevice))
                return;

            nativeDevice.PropertyChanged -= Device_ConnectionStatusChanged;
            nativeDevice.PropertyChanged += Device_ConnectionStatusChanged;

            ConnectedDeviceRegistry[device.Id.ToString()] = device;

            // TODO: ObservableBluetoothLEDevice.ConnectAsync needs updated to include a cancelation token param
            // currently it is hardcoded to 5000ms. On windows users should not use cancellation tokens with 
            // timeouts that are <= 5000ms
            await nativeDevice.ConnectAsync();

            if (nativeDevice.BluetoothLEDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                // use DisconnectDeviceNative to clean up resources otherwise windows won't disconnect the device
                // after a subsequent successful connection (#528, #536, #423)
                DisconnectDeviceNative(device);

                // fire a connection failed event
                HandleConnectionFail(device, "Failed connecting to device.");

                // this is normally done in Device_ConnectionStatusChanged but since nothing actually connected
                // or disconnect, ConnectionStatusChanged will not fire.
                ConnectedDeviceRegistry.TryRemove(device.Id.ToString(), out _);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                // connection attempt succeeded but was cancelled before it could be completed
                // see TODO above.

                // cleanup resources
                DisconnectDeviceNative(device);
            }
            else
            {
                _deviceOperationRegistry[device.Id.ToString()] = device;
            }
        }

        private void Device_ConnectionStatusChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            if (!(sender is ObservableBluetoothLEDevice nativeDevice) || nativeDevice.BluetoothLEDevice == null)
            {
                return;
            }

            if (propertyChangedEventArgs.PropertyName != nameof(nativeDevice.IsConnected))
            {
                return;
            }

            var address = ParseDeviceId(nativeDevice.BluetoothLEDevice.BluetoothAddress).ToString();
            if (nativeDevice.IsConnected && ConnectedDeviceRegistry.TryGetValue(address, out var connectedDevice))
            {
                HandleConnectedDevice(connectedDevice);
                return;
            }

            if (!nativeDevice.IsConnected && ConnectedDeviceRegistry.TryRemove(address, out var disconnectedDevice))
            {
                bool isNormalDisconnect = !_deviceOperationRegistry.Remove(disconnectedDevice.Id.ToString());
                if (!isNormalDisconnect)
                {
                    // device was powered off or went out of range.  Call DisconnectDeviceNative to cleanup
                    // resources otherwise windows will not disconnect on a subsequent connect-disconnect.
                    DisconnectDeviceNative(disconnectedDevice);
                }

                // fire the correct event (DeviceDisconnected or DeviceConnectionLost)
                HandleDisconnectedDevice(isNormalDisconnect, disconnectedDevice);
            }
        }

        protected override void DisconnectDeviceNative(IDevice device)
        {
            // Windows doesn't support disconnecting, so currently just dispose of the device
            Trace.Message($"Disconnected from device with ID:  {device.Id}");
            
            if (device.NativeDevice is ObservableBluetoothLEDevice)
            {
                _deviceOperationRegistry.Remove(device.Id.ToString());
                ((Device)device).ClearServices();

                // [TR 07-25-23] don't actually dispose the device.  Dispose has special meaning on Windows.
                // Once an object is "Disposed" it cannot be accessed in any way.
                ((Device)device).FreeResources();
            }
        }

        public override async Task<IDevice> ConnectToKnownDeviceAsync(Guid deviceGuid, ConnectParameters connectParameters = default, CancellationToken cancellationToken = default)
        {
            //convert GUID to string and take last 12 characters as MAC address
            var guidString = deviceGuid.ToString("N").Substring(20);
            var bluetoothAddress = Convert.ToUInt64(guidString, 16);
            var nativeDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            var knownDevice = new Device(this, nativeDevice, 0, deviceGuid, _dq);

            await ConnectToDeviceAsync(knownDevice, cancellationToken: cancellationToken);
            return knownDevice;
        }

        public override IReadOnlyList<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null)
        {
            //currently no way to retrieve paired and connected devices on windows without using an
            //async method. 
            Trace.Message("Returning devices connected by this app only");
            return ConnectedDevices;
        }

        /// <summary>
        /// Parses a given advertisement for various stored properties
        /// Currently only parses the manufacturer specific data
        /// </summary>
        /// <param name="adv">The advertisement to parse</param>
        /// <returns>List of generic advertisement records</returns>
        public static List<AdvertisementRecord> ParseAdvertisementData(BluetoothLEAdvertisement adv)
        {
            var advList = adv.DataSections;
            return advList.Select(data => new AdvertisementRecord((AdvertisementRecordType)data.DataType, data.Data?.ToArray())).ToList();
        }

        /// <summary>
        /// Handler for devices found when duplicates are not allowed
        /// </summary>
        /// <param name="watcher">The bluetooth advertisement watcher currently being used</param>
        /// <param name="btAdv">The advertisement recieved by the watcher</param>
        private async void DeviceFoundAsync(BluetoothLEAdvertisementWatcher watcher, BluetoothLEAdvertisementReceivedEventArgs btAdv)
        {
            var deviceId = ParseDeviceId(btAdv.BluetoothAddress);

            if (DiscoveredDevicesRegistry.TryGetValue(deviceId, out var device))
            {
                Trace.Message("AdvertisdedPeripheral: {0} Id: {1}, Rssi: {2}", device.Name, device.Id, btAdv.RawSignalStrengthInDBm);
                (device as Device)?.Update(btAdv.RawSignalStrengthInDBm, ParseAdvertisementData(btAdv.Advertisement));
                this.HandleDiscoveredDevice(device);
            }
            else
            {
                var bluetoothLeDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(btAdv.BluetoothAddress);
                if (bluetoothLeDevice != null) //make sure advertisement bluetooth address actually returns a device
                {
                    device = new Device(this, bluetoothLeDevice, btAdv.RawSignalStrengthInDBm, deviceId, _dq, ParseAdvertisementData(btAdv.Advertisement), btAdv.IsConnectable);
                    Trace.Message("DiscoveredPeripheral: {0} Id: {1}, Rssi: {2}", device.Name, device.Id, btAdv.RawSignalStrengthInDBm);
                    this.HandleDiscoveredDevice(device);
                }
            }
        }

        /// <summary>
        /// Method to parse the bluetooth address as a hex string to a UUID
        /// </summary>
        /// <param name="bluetoothAddress">BluetoothLEDevice native device address</param>
        /// <returns>a GUID that is padded left with 0 and the last 6 bytes are the bluetooth address</returns>
        private static Guid ParseDeviceId(ulong bluetoothAddress)
        {
            var macWithoutColons = bluetoothAddress.ToString("x");
            macWithoutColons = macWithoutColons.PadLeft(12, '0'); //ensure valid length
            var deviceGuid = new byte[16];
            Array.Clear(deviceGuid, 0, 16);
            var macBytes = Enumerable.Range(0, macWithoutColons.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(macWithoutColons.Substring(x, 2), 16))
                .ToArray();
            macBytes.CopyTo(deviceGuid, 10);
            return new Guid(deviceGuid);
        }

        public override IReadOnlyList<IDevice> GetKnownDevicesByIds(Guid[] ids)
        {
            // TODO: implement this
            return new List<IDevice>();
        }
    }
}