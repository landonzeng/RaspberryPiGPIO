using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NModbus;
using Serilog;
using System.Device.Gpio;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using static RaspberryPiService.SlaveStorage;

namespace RaspberryPiService;

public class GpioService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private GpioController _gpio;
    private TcpListener _listener;
    private IModbusSlaveNetwork _modbusSlaveNetwork;
    private SlaveStorage _dataStore;

    /// <summary>
    /// GPIO引脚 Board 16
    /// </summary>
    private int _pinIndex;
    /// <summary>
    /// GPIO引脚 Board 40
    /// </summary>
    private int _buzzerPin;
    /// <summary>
    /// 控制湿度自动蜂鸣
    /// </summary>
    private bool _autoBeepEnabled = true;
    /// <summary>
    /// 控制手动蜂鸣
    /// </summary>
    private bool _manualBeepEnabled;
    /// <summary>
    /// 当前蜂鸣器是否在活动中 
    /// </summary>
    private bool _isBuzzerActive;
    /// <summary>
    /// DHT11传感器数据
    /// </summary>
    private int[] _dht11Data = new int[7];
    /// <summary>
    /// 新增标志位，防止自动蜂鸣
    /// </summary>
    private bool _preventAutoBeep = false;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="configuration"></param>
    public GpioService(IConfiguration configuration)
    {
        _configuration = configuration;
        ConfigureLogging();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            InitializeConfiguration();
            InitializeGpio();
            InitializeModbusServer();

            Log.Information("Service is starting...");

            _ = ListenForClientsAsync(stoppingToken);
            _ = MonitorDht11Async(stoppingToken);
            _ = MonitorBuzzerAsync(stoppingToken);

            await _modbusSlaveNetwork.ListenAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while running the service.");
        }
        finally
        {
            CleanupGpio();
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Log配置
    /// </summary>
    private void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Async(x => x.Console())
            .CreateLogger();
    }

    /// <summary>
    /// 初始化配置
    /// </summary>
    private void InitializeConfiguration()
    {
        _pinIndex = int.Parse(_configuration["GPIO:PinIndex"]);
        _buzzerPin = int.Parse(_configuration["GPIO:BuzzerPin"]);

        var serverIp = _configuration["ModbusServer:IPAddress"];
        var serverPort = int.Parse(_configuration["ModbusServer:Port"]);
        _listener = new TcpListener(IPAddress.Parse(serverIp), serverPort);
        _listener.Start();
    }

    /// <summary>
    /// 初始化 Gpio
    /// </summary>
    private void InitializeGpio()
    {
        _gpio = new GpioController(PinNumberingScheme.Board);
        _gpio.OpenPin(_pinIndex);
        _gpio.OpenPin(_buzzerPin, PinMode.Output);
    }

    /// <summary>
    /// 初始化 ModbusServer
    /// </summary>
    private void InitializeModbusServer()
    {
        var factory = new ModbusFactory();
        _dataStore = new SlaveStorage();

        var slave = factory.CreateSlave(1, _dataStore);
        _modbusSlaveNetwork = factory.CreateSlaveNetwork(_listener);
        _modbusSlaveNetwork.AddSlave(slave);

        _dataStore.HoldingRegisters.StorageOperationOccurred += OnModbusWrite;
    }

    /// <summary>
    /// 监听客户端
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    private async Task ListenForClientsAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                Log.Information($"Client connected: {client.Client.RemoteEndPoint}");
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止时，捕获取消异常并退出
            Log.Information("Client listener has been stopped.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while listening for clients.");
        }
    }

    /// <summary>
    /// Modbus写入
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnModbusWrite(object sender, StorageEventArgs<ushort> e)
    {
        if (e.Operation == PointOperation.Write)
        {
            // 显示写入信息
            Log.Information($"{DateTime.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff")} SlaveId: {e.Operation}, Modbus Write Operation: Address:{e.StartingAddress}, Value:{JsonSerializer.Serialize(e.Points)}");


            if (e.StartingAddress == 5 && e.Points.Length > 0)
            {
                int writeValue = e.Points[0];
                _dht11Data[6] = writeValue;  // 更新控制值

                Log.Information($"Modbus Write Operation: Address={e.StartingAddress}, Value={writeValue}");

                switch (writeValue)
                {
                    case 0:
                        Log.Information($"关闭蜂鸣器单次蜂鸣");
                        _manualBeepEnabled = false;
                        ControlBuzzer(false); // 立即关闭蜂鸣器
                        _preventAutoBeep = true; // 阻止自动蜂鸣
                        break;
                    case 1:
                        Log.Information($"启动湿度超过40%自动蜂鸣");
                        _autoBeepEnabled = true;
                        _preventAutoBeep = false; // 允许自动蜂鸣
                        break;
                    case 2:
                        Log.Information($"关闭湿度超过40%自动蜂鸣");
                        _manualBeepEnabled = false;
                        _autoBeepEnabled = false;
                        ControlBuzzer(false); // 立即关闭蜂鸣器
                        break;
                    case 3:
                        Log.Information($"手动启动蜂鸣器蜂鸣");
                        _manualBeepEnabled = true;
                        break;
                }
            }
        }
        else
        {
            // 显示读取信息
            Log.Debug($"湿度：{_dataStore.HoldingRegisters[2]}\t温度：{_dataStore.HoldingRegisters[0]}\t蜂鸣器状态:{_dataStore.HoldingRegisters[4]}\t蜂鸣器控制值:{_dataStore.HoldingRegisters[5]}");
        }

        // 立即更新蜂鸣器状态
        UpdateBuzzerStatus();
    }

    /// <summary>
    /// 更新蜂鸣器状态
    /// </summary>
    private void UpdateBuzzerStatus()
    {
        if (_manualBeepEnabled || _isBuzzerActive)
        {
            _dht11Data[5] = 1; // 蜂鸣器正在活动
        }
        else
        {
            _dht11Data[5] = 0; // 蜂鸣器不活动
        }
    }

    /// <summary>
    /// 监控DHT11
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    private async Task MonitorDht11Async(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(500, stoppingToken);

            if (ReadDht11Data())
            {
                Log.Information($"湿度：{_dht11Data[0]}.{_dht11Data[1]}%\t温度：{_dht11Data[2]}.{_dht11Data[3]}℃\t蜂鸣器状态:{_dht11Data[5]}\t蜂鸣器控制值:{_dht11Data[6]}");
                _dataStore.HoldingRegisters[0] = (ushort)(_dht11Data[2] * 10 + _dht11Data[3]);
                _dataStore.HoldingRegisters[2] = (ushort)(_dht11Data[0] * 10 + _dht11Data[1]);
                _dataStore.HoldingRegisters[4] = (ushort)_dht11Data[5];
                _dataStore.HoldingRegisters[5] = (ushort)_dht11Data[6];

                if (_dht11Data[0] <= 40)
                {
                    _preventAutoBeep = false; // 当湿度低于40%时，允许再次自动蜂鸣
                }

                if (_autoBeepEnabled && !_preventAutoBeep && _dht11Data[0] > 40)
                {
                    ControlBuzzer(true);
                }
                else
                {
                    ControlBuzzer(false);
                }
            }
        }
    }

    /// <summary>
    /// 监控蜂鸣器
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    private async Task MonitorBuzzerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_manualBeepEnabled)
            {
                await BeepPatternAsync(2, 1000);
                _dht11Data[5] = 1; // 确保蜂鸣器状态为1
            }
            else if (_isBuzzerActive)
            {
                await BeepPatternAsync(1, 440);
                _dht11Data[5] = 1; // 确保蜂鸣器状态为1
            }
            else
            {
                _gpio.Write(_buzzerPin, PinValue.Low);
                _dht11Data[5] = 0; // 确保蜂鸣器状态为0
            }
            await Task.Delay(1000, stoppingToken);
        }
    }

    /// <summary>
    /// 控制蜂鸣器
    /// </summary>
    /// <param name="shouldBeep"></param>
    private void ControlBuzzer(bool shouldBeep)
    {
        _isBuzzerActive = shouldBeep;
        _gpio.Write(_buzzerPin, shouldBeep ? PinValue.High : PinValue.Low);
        _dht11Data[5] = shouldBeep ? 1 : 0;
    }

    /// <summary>
    /// 蜂鸣
    /// </summary>
    /// <param name="times"></param>
    /// <param name="frequency"></param>
    /// <returns></returns>
    private async Task BeepPatternAsync(int times, int frequency)
    {
        const int beepDuration = 100; // 100ms
        const int pauseDuration = 200; // 200ms for 3 times in 1 second

        double period = 1.0 / frequency;
        double halfPeriod = period / 2;

        for (int i = 0; i < times; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < beepDuration)
            {
                _gpio.Write(_buzzerPin, PinValue.High);
                await Task.Delay(TimeSpan.FromSeconds(halfPeriod));
                _gpio.Write(_buzzerPin, PinValue.Low);
                await Task.Delay(TimeSpan.FromSeconds(halfPeriod));
            }
            if (i < times - 1) // 不在最后一次蜂鸣后暂停
            {
                await Task.Delay(pauseDuration);
            }
        }
        _gpio.Write(_buzzerPin, PinValue.Low);
    }

    /// <summary>
    /// 读取DHT11数据
    /// </summary>
    /// <returns></returns>
    private bool ReadDht11Data()
    {
        PinValue lastState = PinValue.High;
        int j = 0;
        _dht11Data[0] = _dht11Data[1] = _dht11Data[2] = _dht11Data[3] = _dht11Data[4] = 0;
        _gpio.SetPinMode(_pinIndex, PinMode.Output);
        _gpio.Write(_pinIndex, 0);
        Thread.Sleep(18);
        _gpio.Write(_pinIndex, 1);
        WaitMicroseconds(40);
        _gpio.SetPinMode(_pinIndex, PinMode.Input);

        for (int i = 0; i < 85; i++)
        {
            int counter = 0;
            while (_gpio.Read(_pinIndex) == lastState)
            {
                counter++;
                WaitMicroseconds(1);
                if (counter == 255) break;
            }
            lastState = _gpio.Read(_pinIndex);
            if (counter == 255) break;
            if (i >= 4 && i % 2 == 0)
            {
                _dht11Data[j / 8] <<= 1;
                if (counter > 16)
                {
                    _dht11Data[j / 8] |= 1;
                }
                j++;
            }
        }
        return j >= 40 && _dht11Data[4] == ((_dht11Data[0] + _dht11Data[1] + _dht11Data[2] + _dht11Data[3]) & 0xFF);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="microseconds"></param>
    private void WaitMicroseconds(int microseconds)
    {
        var until = DateTime.UtcNow.Ticks + (microseconds * 10);
        while (DateTime.UtcNow.Ticks < until) { }
    }

    /// <summary>
    /// 
    /// </summary>
    private void CleanupGpio()
    {
        if (_gpio != null)
        {
            if (_gpio.IsPinOpen(_pinIndex)) _gpio.ClosePin(_pinIndex);
            if (_gpio.IsPinOpen(_buzzerPin)) _gpio.ClosePin(_buzzerPin);
        }
    }
}
