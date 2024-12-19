using NModbus;

namespace RaspberryPiService;

public class SlaveStorage : ISlaveDataStore
{
    private readonly SparsePointSource<bool> _coilDiscretes;
    private readonly SparsePointSource<bool> _coilInputs;
    private readonly SparsePointSource<ushort> _holdingRegisters;
    private readonly SparsePointSource<ushort> _inputRegisters;

    public SlaveStorage()
    {
        _coilDiscretes = new SparsePointSource<bool>();
        _coilInputs = new SparsePointSource<bool>();
        _holdingRegisters = new SparsePointSource<ushort>();
        _inputRegisters = new SparsePointSource<ushort>();
    }

    public SparsePointSource<bool> CoilDiscretes => _coilDiscretes;

    public SparsePointSource<bool> CoilInputs => _coilInputs;

    public SparsePointSource<ushort> HoldingRegisters => _holdingRegisters;

    public SparsePointSource<ushort> InputRegisters => _inputRegisters;

    IPointSource<bool> ISlaveDataStore.CoilDiscretes => _coilDiscretes;

    IPointSource<bool> ISlaveDataStore.CoilInputs => _coilInputs;

    IPointSource<ushort> ISlaveDataStore.HoldingRegisters => _holdingRegisters;

    IPointSource<ushort> ISlaveDataStore.InputRegisters => _inputRegisters;

    /// <summary>
    /// Sparse storage for points.
    /// </summary>
    public class SparsePointSource<TPoint> : IPointSource<TPoint>
    {
        private readonly Dictionary<ushort, TPoint> _values = new Dictionary<ushort, TPoint>();

        public event EventHandler<StorageEventArgs<TPoint>> StorageOperationOccurred;

        /// <summary>
        /// Gets or sets the value of an individual point wih tout 
        /// </summary>
        /// <param name="registerIndex"></param>
        /// <returns></returns>
        public TPoint this[ushort registerIndex]
        {
            get
            {
                TPoint value;

                if (_values.TryGetValue(registerIndex, out value))
                    return value;

                return default(TPoint);
            }
            set { _values[registerIndex] = value; }
        }

        public TPoint[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            var points = new TPoint[numberOfPoints];

            for (ushort index = 0; index < numberOfPoints; index++)
            {
                points[index] = this[(ushort)(index + startAddress)];
            }

            StorageOperationOccurred?.Invoke(this,
                new StorageEventArgs<TPoint>(PointOperation.Read, startAddress, points));

            return points;
        }

        public void WritePoints(ushort startAddress, TPoint[] points)
        {
            for (ushort index = 0; index < points.Length; index++)
            {
                this[(ushort)(index + startAddress)] = points[index];
            }

            StorageOperationOccurred?.Invoke(this,
                new StorageEventArgs<TPoint>(PointOperation.Write, startAddress, points));
        }
    }

    public class StorageEventArgs<TPoint> : EventArgs
    {
        private readonly PointOperation _pointOperation;
        private readonly ushort _startingAddress;
        private readonly TPoint[] _points;

        public StorageEventArgs(PointOperation pointOperation, ushort startingAddress, TPoint[] points)
        {
            _pointOperation = pointOperation;
            _startingAddress = startingAddress;
            _points = points;
        }

        public ushort StartingAddress => _startingAddress;

        public TPoint[] Points => _points;

        public PointOperation Operation => _pointOperation;
    }

    public enum PointOperation
    {
        Read,
        Write
    }
}