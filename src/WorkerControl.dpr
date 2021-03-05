program WorkerControl;

uses
  Vcl.SvcMgr,
  uMain in 'uMain.pas' {WorkerControlService: TService},
  WorkerControl.BoostWorkerGroup in 'WorkerControl.BoostWorkerGroup.pas',
  WorkerControl.Config in 'WorkerControl.Config.pas',
  WorkerControl.KeepAliveMessage in 'WorkerControl.KeepAliveMessage.pas',
  WorkerControl.Manager in 'WorkerControl.Manager.pas',
  WorkerControl.SafeStopMessage in 'WorkerControl.SafeStopMessage.pas',
  WorkerControl.Worker in 'WorkerControl.Worker.pas',
  WorkerControl.WorkerGroup in 'WorkerControl.WorkerGroup.pas';

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
