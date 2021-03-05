unit WorkerControl.SafeStopMessage;

interface

uses
  JSON;

type
  TSafeStopMessage = class
  private
    FText: string;
    procedure SetText(const Value: string);
  public
    property Text : string read FText write SetText;
    function ToJSON : TJSONObject;
  end;

implementation

{ TSafeStopMessage }

procedure TSafeStopMessage.SetText(const Value: string);
begin
  FText := Value;
end;

function TSafeStopMessage.ToJSON: TJSONObject;
begin
  Result := TJSONObject.Create;
  Result.AddPair('Text', TJSONString.Create(FText));
end;

end.
