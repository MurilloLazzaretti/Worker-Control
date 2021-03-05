unit uMain;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Classes,
  Vcl.Graphics, Vcl.Controls, Vcl.SvcMgr, Vcl.Dialogs, WorkerControl.Manager;

type
  TWorkerControlService = class(TService)
    procedure ServiceStart(Sender: TService; var Started: Boolean);
    procedure ServiceStop(Sender: TService; var Stopped: Boolean);
  private
    { Private declarations }
  public
    Manager : TManager;
    function GetServiceController: TServiceController; override;
  end;

var
  WorkerControlService: TWorkerControlService;

implementation

{$R *.dfm}

procedure ServiceController(CtrlCode: DWord); stdcall;
begin
  WorkerControlService.Controller(CtrlCode);
end;

function TWorkerControlService.GetServiceController: TServiceController;
begin
  Result := ServiceController;
end;

procedure TWorkerControlService.ServiceStart(Sender: TService;
  var Started: Boolean);
begin
  Manager := TManager.Create;
  Manager.Start;
  Started := True;
end;

procedure TWorkerControlService.ServiceStop(Sender: TService;
  var Stopped: Boolean);
begin
  Manager.Stop;
  Manager.Free;
  Stopped := True;
end;

end.
