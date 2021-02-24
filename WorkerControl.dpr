program WorkerControl;

{$APPTYPE CONSOLE}

{$R *.res}

uses
  WorkerControl.Worker in 'src\WorkerControl.Worker.pas',
  WorkerControl.WorkerGroup in 'src\WorkerControl.WorkerGroup.pas',
  WorkerControl.Manager in 'src\WorkerControl.Manager.pas',
  WorkerControl.KeepAliveMessage in 'src\WorkerControl.KeepAliveMessage.pas',
  WorkerControl.Config in 'src\WorkerControl.Config.pas';

var
  Manager : TManager;
begin
  ReportMemoryLeaksOnShutdown := True;
  Manager := TManager.Create;
  Manager.Start;
  Readln;
  Manager.Stop;
  Manager.Free;
end.
