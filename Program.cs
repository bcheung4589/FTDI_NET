using FTDI_NET;

// attempt connecting to scale
using var scaleService = new ScaleService();
if (!(await scaleService.AsyncTryConnect()))
{
    return;
}

// hook up to the data received-event
scaleService.OnDataReceived += (object sender, DataReceivedEventArgs e) =>
{
    Console.WriteLine("{0}: {1}", e.DeviceId, e.Data);
};

// wait for input/output
Console.ReadKey();