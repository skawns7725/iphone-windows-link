# PhotoReceiver

공식 LocalSend iOS 앱에서 Windows PC로 파일을 받는 .NET 8 콘솔 앱입니다. [LocalSend v2 명세](https://github.com/localsend/protocol/blob/main/README.md)의 IPv4 디스커버리와 업로드를 지원합니다. 기기 이름은 Windows 컴퓨터 이름이며, 실행할 때 자체 서명 TLS 인증서와 인증서의 SHA-256 지문을 생성합니다.

## Windows에서 실행

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)를 설치합니다.
2. 저장소 루트에서 PowerShell로 다음 명령을 실행합니다.

   ```powershell
   dotnet run --project src/PhotoReceiver
   ```

3. Windows Defender 방화벽 알림이 뜨면 **개인 네트워크**에서 허용합니다. 직접 규칙을 설정하는 경우 로컬 서브넷의 UDP 및 TCP 인바운드 포트 53317을 허용합니다. 동일한 포트를 사용하는 Windows용 LocalSend가 실행 중이면 종료한 뒤 시작합니다.

수신 경로는 기본적으로 `%USERPROFILE%\Downloads\PhonePhotos`입니다. 필요하면 실행 인수로 변경할 수 있습니다.

```powershell
dotnet run --project src/PhotoReceiver -- --ReceiveDirectory="C:\Temp\PhonePhotos"
```

유효한 전송 요청의 파일을 모두 자동 수락합니다. 신뢰할 수 있는 개인 Wi-Fi에서 실행합니다. 파일마다 실제 저장 이름, 바이트 수, 수신 소요 시간이 출력됩니다. 동일 이름이 이미 있으면 번호를 붙이고, 발신자가 SHA-256을 제공한 경우 일치한 파일만 저장합니다. 콘솔에서 `Ctrl+C`로 종료합니다.

## 공식 iOS 앱으로 확인

1. iPhone과 Windows PC를 같은 Wi-Fi에 연결하고 PC에서 PhotoReceiver를 실행합니다.
2. iPhone에서 [공식 LocalSend 앱](https://apps.apple.com/app/localsend/id1661733229)을 열고 로컬 네트워크 접근을 허용합니다.
3. **보내기**에서 사진 한 장을 선택한 뒤 주변 기기 목록에서 콘솔에 표시된 PC 이름을 선택합니다.
4. iPhone에 전송 완료가 표시되고 콘솔에 파일명·크기·소요 시간이 출력되는지 확인합니다. 저장 폴더에서 사진을 열어 확인합니다.

### 실패 시 Wireshark 비교

Windows Wi-Fi 인터페이스에서 표시 필터 `udp.port == 53317 || tcp.port == 53317`을 사용합니다. 먼저 PhotoReceiver에 대한 전송을 캡처합니다. PhotoReceiver를 종료한 뒤 같은 PC에 공식 LocalSend Windows 앱을 실행하고 같은 사진으로 정상 전송을 캡처해 두 흐름을 비교합니다.

- PC가 목록에 없으면 `224.0.0.167:53317`의 `announce: true` 메시지와 UDP `announce: false` 응답 또는 TCP 등록 연결을 비교합니다. 패킷이 누락되면 iOS 로컬 네트워크 권한, Windows 방화벽, Wi-Fi의 기기 간 통신 차단 설정을 확인합니다.
- PC가 목록에 있는데 전송에 실패하면 TCP 연결과 TLS 핸드셰이크가 어느 단계에서 끊기는지 확인합니다. HTTPS 요청 경로와 응답 코드는 서버 콘솔 로그에서 확인합니다. 정상 전송은 `prepare-upload`와 `upload`에 각각 HTTP 200을 반환합니다. HTTP 403이면 세션·토큰 또는 발신 IP를, 422이면 바이트 수와 SHA-256을 확인합니다.

## 단위 테스트

저장소 루트에서 실행합니다.

```powershell
dotnet test
```

멀티캐스트 JSON 파싱, 자기 알림 무시, 잘못된 패킷 처리, 파일별 토큰 및 세션 ID, 준비 요청 메타데이터를 검증합니다.
