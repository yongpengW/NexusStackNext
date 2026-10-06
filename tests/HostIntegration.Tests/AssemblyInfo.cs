// Each worker remains serial. The owned local controller alone schedules bounded ordinary-journey lanes.
// Dedicated migration/restart journeys run exclusively; expensive preparation has a cross-process permit.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
