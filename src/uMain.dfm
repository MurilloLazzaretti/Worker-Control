object WorkerControlService: TWorkerControlService
  OldCreateOrder = False
  AllowPause = False
  DisplayName = 'WorkerControl'
  OnStart = ServiceStart
  OnStop = ServiceStop
  Height = 311
  Width = 328
end
