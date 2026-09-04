using System;

namespace O2P.Domain.Entities
{
    // Single-row control record (Id is always 1) used to signal running Worker nodes out-of-band via
    // the shared metadata DB. Setting RestartRequestedAt to a time later than a Worker's own start
    // time tells that Worker to shut down so its supervisor relaunches it on fresh code/state.
    public class WorkerControl
    {
        public int Id { get; set; }
        public DateTimeOffset? RestartRequestedAt { get; set; }
    }
}
