using FTD2XX_NET;
using System.ComponentModel;
using System.Text;

namespace FTDI_NET;

/// <summary>
/// EventArgs class for the Data Received-event.
/// </summary>
public class DataReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Id of the device that send the data.
    /// </summary>
    public string DeviceId { get; set; } = null!;

    /// <summary>
    /// Send data in raw format.
    /// </summary>
    public string Data { get; set; } = null!;
}

/// <summary>
/// Delegate for the Data Received-event for Scales.
/// </summary>
/// <param name="sender"></param>
/// <param name="e"></param>
public delegate void ScaleDataReceivedEventHandler(object sender, DataReceivedEventArgs e);

/// <summary>
/// The ScaleService connects to "FT230X Basic UART"-scales and waits to receive data.
/// Hook up to the OnDataReceived-event to catch the received data.
/// Requires FTDI Drivers which can be found at <see cref="https://eu-en.ohaus.com/en-EU/Support/Software-and-Drivers"/>.
/// </summary>
public class ScaleService : IDisposable
{
    /// <summary>
    /// FTDI Handler for communication with the FTDI chip.
    /// </summary>
    private FTDI _ftdi = null!;

    /// <summary>
    /// Wait Handle for listening to incoming data.
    /// </summary>
    private AutoResetEvent _receivedDataEvent = null!;

    /// <summary>
    /// The BackgroundWorker creates a seperate thread for listening to incoming data.
    /// </summary>
    private BackgroundWorker _dataReceivedHandler = null!;

    /// <summary>
    /// The Scale series that is supported.
    /// </summary>
    private const string SCALE_DESCRIPTION = "FT230X Basic UART";

    /// <summary>
    /// Maximal retry attempts before stopping execution.
    /// </summary>
    private const int RETRY_TRESHOLD = 3;

    /// <summary>
    /// Amount of seconds to wait before retrying.
    /// </summary>
    private const int RETRY_WAIT = 5;

    /// <summary>
    /// Internal counter for retrying.
    /// </summary>
    private int _retryCounter = 0;

    /// <summary>
    /// Any error that happens internally.
    /// </summary>
    public string? ErrorMessage;

    /// <summary>
    /// The EventHandler for receiving data.
    /// </summary>
    public event ScaleDataReceivedEventHandler OnDataReceived = null!;

    /// <summary>
    /// Call the Data Received-EventHandler with the given EventArgs.
    /// </summary>
    /// <param name="e"></param>
    protected virtual void DataReceived(DataReceivedEventArgs e) => OnDataReceived?.Invoke(this, e);

    /// <summary>
    /// Try connecting to the Scale.
    /// </summary>
    /// <returns></returns>
    public async Task<bool> TryConnectAsync()
    {
        ErrorMessage = null;

        if (Connect())
        {
            return true;
        }

        if (_retryCounter >= RETRY_TRESHOLD)
        {
            ErrorMessage = "Connecting to FTDI devices failed!";
            Console.WriteLine(ErrorMessage);
            return false;
        }

        _retryCounter++;
        ErrorMessage = $"Failed connecting, retrying in {RETRY_WAIT} seconds.. ({_retryCounter}/{RETRY_TRESHOLD})";
        Console.WriteLine(ErrorMessage);
        await Task.Delay(RETRY_WAIT * 1000);

        return await TryConnectAsync();
    }

    /// <summary>
    /// Connect to the Scale and start listening for data.
    /// </summary>
    /// <returns></returns>
    public bool Connect()
    {
        _ftdi ??= new FTDI();

        // check for any devices
        var deviceCount = CountDevices();
        if (deviceCount <= 0)
        {
            ErrorMessage = "No devices to found.";
            Console.WriteLine(ErrorMessage);
            return false;
        }

        // check for supported devices
        var list = GetDeviceInfoList();
        var foundDevice = list?.FirstOrDefault(x => x.Description.Equals(SCALE_DESCRIPTION));
        if (foundDevice == null)
        {
            ErrorMessage = "Device not supported.";
            Console.WriteLine(ErrorMessage);
            return false;
        }

        // connect to device
        FTDI.FT_STATUS ftStatus = _ftdi.OpenByDescription(SCALE_DESCRIPTION);
        if (ftStatus != FTDI.FT_STATUS.FT_OK)
        {
            ErrorMessage = "Error connecting to FTDI chip.";
            Console.WriteLine(ErrorMessage);
            return false;
        }

        Console.WriteLine("Connected to devices: {0}", deviceCount);

        // register event for data receiving
        _receivedDataEvent = new AutoResetEvent(false);
        _ftdi.SetEventNotification(FTDI.FT_EVENTS.FT_EVENT_RXCHAR, _receivedDataEvent);

        // create background worker to listen to EventNotifications
        _dataReceivedHandler = new BackgroundWorker();
        _dataReceivedHandler.DoWork += ReadData;
        if (!_dataReceivedHandler.IsBusy)
        {
            // start listening
            _dataReceivedHandler.RunWorkerAsync();
        }

        // purge buffers
        do
        {
            ftStatus = _ftdi.StopInTask();
        } while (ftStatus != FTDI.FT_STATUS.FT_OK);

        _ = _ftdi.Purge(FTDI.FT_PURGE.FT_PURGE_RX | FTDI.FT_PURGE.FT_PURGE_TX);
        do
        {
            ftStatus = _ftdi.RestartInTask();
        } while (ftStatus != FTDI.FT_STATUS.FT_OK);

        // set read latency
        _ftdi.SetLatency(1);

        return _ftdi.IsOpen;
    }

    /// <summary>
    /// Count the available devices.
    /// </summary>
    /// <returns></returns>
    private int CountDevices()
    {
        uint deviceCount = 0;
        var ftStatus = _ftdi.GetNumberOfDevices(ref deviceCount);
        if (ftStatus != FTDI.FT_STATUS.FT_OK)
        {
            return 0;
        }

        if (deviceCount < 1)
        {
            return 0;
        }

        return (int)deviceCount;
    }

    /// <summary>
    /// Get the information of the available devices.
    /// </summary>
    /// <returns></returns>
    private FTDI.FT_DEVICE_INFO_NODE[]? GetDeviceInfoList()
    {
        var deviceCount = CountDevices();
        var deviceList = new FTDI.FT_DEVICE_INFO_NODE[deviceCount];
        var ftStatus = _ftdi.GetDeviceList(deviceList);
        if (ftStatus != FTDI.FT_STATUS.FT_OK)
        {
            return null;
        }
        return deviceList;
    }

    /// <summary>
    /// Wait for receiving data. When data is read, DataReceived(e) is called.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="eventArgs"></param>
    private void ReadData(object? sender, DoWorkEventArgs eventArgs)
    {
        uint bytesAvailable = 0;
        while (true)
        {
            // wait for FTDI event notifications (signals)
            _receivedDataEvent.WaitOne();

            // check if data is available
            FTDI.FT_STATUS status = _ftdi.GetRxBytesAvailable(ref bytesAvailable);
            if (status != FTDI.FT_STATUS.FT_OK)
            {
                continue;
            }
            if (bytesAvailable < 1)
            {
                continue;
            }

            // read the data
            uint numBytesRead = 0;
            var readData = new byte[bytesAvailable];
            status = _ftdi.Read(readData, bytesAvailable, ref numBytesRead);
            if (status != FTDI.FT_STATUS.FT_OK || numBytesRead < 1)
            {
                continue;
            }

            // convert to string and trim empty spaces
            var data = Encoding.UTF8.GetString(readData).Trim().Replace(" ", "");
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            // get the serialNr to identify the sender and trigger DataReceived(e)
            _ftdi.GetSerialNumber(out string serialNr);
            DataReceived(new DataReceivedEventArgs
            {
                DeviceId = serialNr,
                Data = data
            });
        }
    }

    /// <summary>
    /// Release all resources used by current ScaleService.
    /// </summary>
    public void Dispose()
    {
        _ftdi?.Close();
        _receivedDataEvent?.Close();
    }
}
