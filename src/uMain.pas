unit uMain;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Classes,
  Vcl.Graphics, Vcl.Controls, Vcl.SvcMgr, Vcl.Dialogs, WorkerControl.Manager;

type
  TWorkerControlService = class(TService)
    procedure ServiceStart(Sender: TService; var Started: Boolean);
    procedure ServiceStop(Sender: TService; var Stopped: Boolean);
    procedure ServiceDestroy(Sender: TObject);
    procedure ServiceCreate(Sender: TObject);
  private
    FManager : TManager;
  public
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

procedure TWorkerControlService.ServiceCreate(Sender: TObject);
begin
  FManager := TManager.Create;
end;

procedure TWorkerControlService.ServiceDestroy(Sender: TObject);
begin
  FreeAndNil(FManager);
end;

procedure TWorkerControlService.ServiceStart(Sender: TService;
  var Started: Boolean);
begin
  try
    FManager.Start;
    Started := True;
  except
    on E: Exception do
    begin
      OutputDebugString(PChar('Erro em ServiceStart: ' + E.Message));
      Started := False;
    end;
  end;
end;

procedure TWorkerControlService.ServiceStop(Sender: TService;
  var Stopped: Boolean);
begin
  try
    FManager.Stop;
    Stopped := True;
  except
    on E: Exception do
    begin
      OutputDebugString(PChar('Erro em ServiceStop: ' + E.Message));
      Stopped := False;
    end;
  end;
end;

end.
