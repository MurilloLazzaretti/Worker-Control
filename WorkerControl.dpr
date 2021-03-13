program WorkerControl;

uses
  Vcl.SvcMgr,
  uMain in 'src\uMain.pas' {WorkerControlService: TService},
  WorkerControl.BoostWorkerGroup in 'src\WorkerControl.BoostWorkerGroup.pas',
  WorkerControl.Config in 'src\WorkerControl.Config.pas',
  WorkerControl.KeepAliveMessage in 'src\WorkerControl.KeepAliveMessage.pas',
  WorkerControl.Manager in 'src\WorkerControl.Manager.pas',
  WorkerControl.SafeStopMessage in 'src\WorkerControl.SafeStopMessage.pas',
  WorkerControl.Worker in 'src\WorkerControl.Worker.pas',
  WorkerControl.WorkerGroup in 'src\WorkerControl.WorkerGroup.pas',
  WorkerControl.StatusMessage in 'src\WorkerControl.StatusMessage.pas';

{$R *.RES}

begin
  // Windows 2003 Server requires StartServiceCtrlDispatcher to be
  // called before CoRegisterClassObject, which can be called indirectly
  // by Application.Initialize. TServiceApplication.DelayInitialize allows
  // Application.Initialize to be called from TService.Main (after
  // StartServiceCtrlDispatcher has been called).
  //
  // Delayed initialization of the Application object may affect
  // events which then occur prior to initialization, such as
  // TService.OnCreate. It is only recommended if the ServiceApplication
  // registers a class object with OLE and is intended for use with
  // Windows 2003 Server.
  //
  // Application.DelayInitialize := True;
  //
  if not Application.DelayInitialize or Application.Installing then
    Application.Initialize;
  Application.CreateForm(TWorkerControlService, WorkerControlService);
  Application.Run;
end.
