namespace OpenEC.Monitor.Observation;

public enum SlaveAlState : byte { Unknown = 0, Init = 1, PreOp = 2, Boot = 3, SafeOp = 4, Op = 8 }
