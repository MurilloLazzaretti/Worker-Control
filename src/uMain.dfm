object WorkerControlService: TWorkerControlService
  OldCreateOrder = False
  OnCreate = ServiceCreate
  OnDestroy = ServiceDestroy
  AllowPause = False
  DisplayName = 'WorkerControl'
  OnStart = ServiceStart
  OnStop = ServiceStop
  Height = 311
  Width = 328
end
