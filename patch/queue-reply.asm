; Extension for the verified JTDX 2.2.160-32A-rc10 build (see build-native.cjs).
; UDP 52 has the layout of Reply (4), with markers for a saved caller,
; a visible CQ or a direct call. UDP 53 controls native sequencing during AutoCQ. Both are accepted
; only from IPv4 loopback and the configured local server port 2247.
; Existing Reply (4), including requests from LogHX, keeps its behavior.
bits 64
org 0x142d17000

%define parse_message   0x1401edd20
%define after_datagram  0x1401eefcb
%define after_reader    0x1401edd3b
%define jump_table      0x141378a50
%define reply_case      0x1401ee210
%define ignore_packet   0x1401edd56
%define cleanup_reply   0x1401ee471
%define use_reply       0x1401ee2d6
%define after_flag      0x1401ee2eb
%define after_window    0x14009f3c5
%define after_line      0x14009f781
%define process_message 0x1400c3320
%define after_process   0x14009f866
%define prompt_to_log   0x14031b220
%define after_reply_tx_gate 0x1400c4fd7
%define get_module      0x142c1ed78
%define get_symbol      0x142c1ed80
%define plain_text      0x142c20c98
%define string_left     0x142c1e1b8
%define string_mid      0x142c1e1b0
%define auto_stop       0x14009abf0
%define auto_cleanup    0x1400a8f00
%define qstring_from_c  0x1402d91b0
%define qstring_destroy 0x1402d91f0
%define after_udp_accept 0x14009ef68
%define reply_epilogue  0x14009ef93
%define line_clear      0x142c20670
%define line_set        0x142c20680
%define line_text       0x142c20c78
%define tab_current     0x142c208f8
%define button_check    0x142c1fc40
%define button_checked  0x142c209e8
%define button_click    0x142c1fc70
%define generate_tx     0x1400f9290
%define choose_tx1      0x1400d08d0
%define qso_log_data    0x14012be80
%define qstring_assign  0x142c1d528
%define radio_time_ms   0x1401595b0
%define qstring_null    0x142c1c8c0
%define date_time_ms    0x142c1e378
%define saved_caller    0xa8
%define visible_cq      0xa9
%define direct_call     0xaa

%macro iat_call 1
    db 0xff, 0x15                 ; call qword [RIP + disp32]
    dd %1 - ($ + 4)
%endmacro
%macro iat_load 1
    db 0x48, 0x8b, 0x05           ; mov rax, [RIP + disp32]
    dd %1 - ($ + 4)
%endmacro
%macro entry 1
    dd %1 - $$
%endmacro

; The offsets at the front are consumed by the builder and offline harness.
entry check_datagram
entry init_reader
entry dispatch
entry validate_reply
entry set_internal_flag
entry read_window
entry read_line
entry authorized_sender
entry valid_decode_line
entry select_station
entry after_counters
entry select_lease
entry control_packet
entry resolve_tick
entry text_section_end
entry renew_lease
entry renew_end
entry resolve_body
entry resolve_end
entry after_counters_original
entry managed_counter_stop
entry counter_stop_end
entry run_reply
entry reply_tx_gate
entry defer_ptt
entry stop_cq_retry
entry stop_call_retry
entry stop_report_retry
entry skip_early_rr73_stop
entry keep_terminal_counter
entry repeat_final_73
entry hold_s73
entry after_accept
entry valid_direct_call
entry first_recent_log_gate
entry second_recent_log_gate
entry new_exchange_in_history
entry new_exchange_end
entry log_with_sent_report
entry track_report
entry track_report_end
entry remember_report
entry remember_roger_report

; Mutable state is placed in a separate non-executable section.
select_until equ 0x142d18000
tick_function equ select_until + 8
lease_flags equ $$ + (select_until - 0x142d17000) + 16
last_report equ lease_flags + 8
report_slot equ last_report + 8
report_epoch equ report_slot + 4
reports equ report_epoch + 12

; Recent reports use 64 bytes per station. UTC milliseconds, first
; transmission seconds, ASCII report, frequency, mode/length, generation,
; and up to 16 UTF-16 callsign characters. The cache lives in .acqdata.
%define report_rows 48
%define report_size 64
%define report_lifetime 300000

%macro state_load 2
    db %1
    dd %2 - ($ + 4)
%endmacro

%macro call_tick 0
    iat_call tick_function
%endmacro

align 16, db 0x90
check_datagram:
    mov rdx, [rsp+0x40]             ; QByteArray::d
    cmp dword [rdx+4], 12
    jl .pass_through
    add rdx, [rdx+16]
    mov eax, [rdx+8]
    cmp eax, 0x34000000            ; big endian type 52
    je .check_source
    cmp eax, 0x35000000            ; big endian type 53
    jne .pass_through
.check_source:
    cmp dword [rdx], 0xdacbbcAD
    jne .discard

    ; MessageClient::impl server port and readDatagram sender port.
    movzx ecx, word [rsi+0x30]
    movzx edx, word [rsp+0x3e]
    cmp ecx, 2247
    jne .discard
    cmp edx, ecx
    jne .discard

    lea rcx, [rel network_dll]
    iat_call get_module
    test rax, rax
    jz .discard
    mov rcx, rax
    lea rdx, [rel ipv4_export]
    iat_call get_symbol
    test rax, rax
    jz .discard
    lea rcx, [rsp+0x48]             ; QHostAddress written by readDatagram
    lea rdx, [rsp+0x3d]
    mov byte [rdx], 0
    call rax
    mov r8d, eax
    movzx r9d, byte [rsp+0x3d]
    movzx ecx, word [rsi+0x30]
    movzx edx, word [rsp+0x3e]
    call authorized_sender
    test eax, eax
    jz .discard
    mov rdx, [rsp+0x40]
    mov ecx, [rdx+4]
    add rdx, [rdx+16]
    cmp dword [rdx+8], 0x35000000
    jne .pass_through
    mov [rsp+0x28], rdx
    mov [rsp+0x30], ecx
    call resolve_tick
    test eax, eax
    jz .discard
    mov rcx, [rsp+0x28]
    mov edx, [rsp+0x30]
    call control_packet
    jmp .discard
.pass_through:
    lea rdx, [rsp+0x40]
    mov rcx, rsi
    call parse_message
.discard:
    jmp after_datagram

init_reader:
    mov byte [rsp+0x61], 0
    lea rbx, [rsp+0xf0]
    jmp after_reader

dispatch:
    cmp eax, 48                   ; message type after original subtraction of 4
    je .from_queue
    cmp eax, 47
    ja ignore_packet
    jmp 0x1401eddf9               ; original jump table
.from_queue:
    mov byte [rsp+0x61], 1
    jmp reply_case

validate_reply:
    test eax, eax
    jz cleanup_reply
    cmp byte [rsp+0x61], 0
    je use_reply
    cmp eax, 2                    ; require a complete datagram, not "Short"
    jne cleanup_reply
    cmp byte [rsp+0x63], saved_caller
    je use_reply
    cmp byte [rsp+0x63], visible_cq
    je use_reply
    cmp byte [rsp+0x63], direct_call
    jne cleanup_reply
    jmp use_reply

set_internal_flag:
    movzx eax, byte [rsp+0x63]
    cmp byte [rsp+0x61], 1
    je .store
    cmp al, saved_caller
    je .clear_marker
    cmp al, visible_cq            ; ordinary Reply cannot opt into this path
    je .clear_marker
    cmp al, direct_call
    jne .store
.clear_marker:
    xor eax, eax
.store:
    mov [rsp+0x38], al
    jmp after_flag

after_accept:
    ; Runs inside replyToUDP after Configuration::accept_udp_requests().
    ; The ninth argument can carry this marker only for source-checked UDP 52.
    cmp byte [rsp+0x290], direct_call
    je .direct
    mov eax, [rbx+0x594]
    mov byte [rbx+0x64c], 0
    jmp after_udp_accept
.direct:
    cmp byte [rbx+0x2e44], 0       ; transmitter already active
    jne reply_epilogue
    cmp byte [rbx+0x2e47], 0       ; TX watchdog tripped
    jne reply_epilogue
    cmp byte [rbx+0x5f8], 0        ; tune
    jne reply_epilogue
    mov rax, [rbx+0x2ae8]         ; m_mode
    cmp dword [rax+4], 3
    jne reply_epilogue
    add rax, [rax+0x10]
    cmp dword [rax], 0x00540046   ; UTF-16 "FT"
    jne reply_epilogue
    cmp word [rax+4], '8'
    je .valid_mode
    cmp word [rax+4], '4'
    jne reply_epilogue
.valid_mode:
    mov rdx, [rsp+0x280]          ; text argument, a QString with only the DX call
    mov rax, [rdx]
    mov edx, [rax+4]
    add rax, [rax+0x10]
    mov rcx, rax
    call valid_direct_call
    test eax, eax
    jz reply_epilogue

    ; Use the same controls as the operator: clear the locator, set DX Call,
    ; generate standard messages and select Tx1. Keep the TX period and audio.
    mov rax, [rbx+0x90]
    mov rcx, [rax+0x7a8]
    iat_call line_clear
    mov rax, [rbx+0x90]
    mov rcx, [rax+0x7b0]
    mov rdx, [rsp+0x280]
    iat_call line_set
    mov rcx, rbx
    call generate_tx
    mov rcx, rbx
    call choose_tx1

    ; The second TX tab uses its generated text field for the same Tx1.
    mov rax, [rbx+0x90]
    mov rcx, [rax+0xa30]
    iat_call tab_current
    cmp eax, 1
    jne .enable
    mov rax, [rbx+0x90]
    mov rdx, [rax+0xa98]
    lea rcx, [rsp+0x150]
    iat_call line_text
    mov rax, [rbx+0x90]
    mov rcx, [rax+0xbf0]
    lea rdx, [rsp+0x150]
    iat_call line_set
    lea rcx, [rsp+0x150]
    call qstring_destroy
    mov dword [rbx+0x4f4], 7
    mov rax, [rbx+0x90]
    mov rcx, [rax+0xbf8]
    mov edx, 1
    iat_call button_check
.enable:
    mov rax, [rbx+0x90]
    mov rcx, [rax+0x9d0]
    iat_call button_checked
    test al, al
    jnz .report_status
    mov rax, [rbx+0x90]
    mov rcx, [rax+0x9d0]
    iat_call button_click
    jmp reply_epilogue
.report_status:
    mov rcx, rbx
    call 0x140316690              ; statusUpdate, TX was already enabled
    jmp reply_epilogue

; Validate the UTF-16 DX field before touching any controls.
valid_direct_call:
    xor eax, eax
    cmp edx, 3
    jl .done
    cmp edx, 11
    jg .done
    xor r8d, r8d
    xor r9d, r9d
.next:
    movzx r10d, word [rcx+r9*2]
    cmp r10d, '0'
    jb .slash
    cmp r10d, '9'
    jbe .digit
    cmp r10d, 'A'
    jb .done
    cmp r10d, 'Z'
    ja .done
    or r8d, 1
    jmp .advance
.digit:
    or r8d, 2
    jmp .advance
.slash:
    cmp r10d, '/'
    jne .done
    test r9d, r9d
    jz .done
    lea r11d, [r9+1]
    cmp r11d, edx
    je .done
    test r8b, 4
    jnz .done
    or r8d, 4
.advance:
    inc r9d
    cmp r9d, edx
    jb .next
    and r8d, 3
    cmp r8d, 3
    sete al
.done:
    ret

read_window:
    cmp byte [rsp+0x290], saved_caller ; ninth Win64 argument in this frame
    jne .original
    mov rdx, [rsp+0xc8]            ; formatted received line
    mov rax, [rdx]                 ; QString::d in the installed Qt5
    mov edx, [rax+4]               ; length in UTF-16 code units
    add rax, [rax+16]
    mov rcx, rax
    call valid_decode_line
    test eax, eax
    jz .invalid_saved
    lea rcx, [rsp+0x150]
    mov rdx, [rsp+0xc8]
    mov r8d, -1
    iat_call string_left
    jmp after_window
.invalid_saved:
    ; Keep the original 'decode not found' reporting path. An invalid
    ; extended request must never fall through to a visible window reply.
    mov byte [rsp+0x290], 0
    lea rcx, [rsp+0x150]
    mov rdx, [rsp+0xc8]
    xor r8d, r8d
    iat_call string_left
    jmp after_window
.original:
    cmp byte [rsp+0x290], visible_cq
    je .use_window
    mov byte [rsp+0x290], 0
.use_window:
    lea rcx, [rsp+0x150]
    mov rax, [rbx+0x90]
    mov rdx, [rax+0x638]
    iat_load plain_text
    mov [rsp+0xd0], rax
    call rax
    jmp after_window

read_line:
    cmp byte [rsp+0x290], saved_caller
    jne .original
    ; Present the same saved row to the original line extraction. With no
    ; newline, its mid(0, -1) takes the whole row. Keep all constructors and
    ; cleanup in replyToUDP on their original paths.
    mov rcx, rdi
    mov rdx, [rsp+0xc8]
    xor r8d, r8d
    mov r9d, -1
    iat_call string_mid
    jmp after_line
.original:
    mov rax, [rbx+0x90]
    mov rcx, rdi
    mov rdx, [rax+0x638]
    mov rax, [rsp+0xd0]
    call rax
    jmp after_line

run_reply:
    mov rcx, rbx
    cmp byte [rsp+0x290], saved_caller
    je .managed
    cmp byte [rsp+0x290], visible_cq
    je .managed
    call process_message
    jmp after_process
.managed:
    call process_message
managed_reply_return:
    jmp after_process

reply_tx_gate:
    ; The original processMessage frame pushes eight registers and
    ; allocates 0x508 bytes. Match the immediate caller's return address
    ; to the authenticated Reply path so other calls read PromptToLog.
    lea rax, [rel managed_reply_return]
    cmp [rsp+0x548], rax
    je .enable
    call prompt_to_log
    jmp after_reply_tx_gate
.enable:
    mov eax, 1
    jmp after_reply_tx_gate

; Both leaf functions are executed as assembled in the offline tests.
; ecx=configured port, edx=sender port, r8d=IPv4 address, r9d=IPv4 conversion ok.
authorized_sender:
    xor eax, eax
    cmp ecx, 2247
    jne .end
    cmp edx, ecx
    jne .end
    cmp r8d, 0x7f000001
    jne .end
    test r9d, r9d
    setnz al
.end:
    ret

; rcx=UTF-16 line, edx=length. FT8/FT4 only. A packet cannot insert other
; lines or rich text. The companion additionally checks the decoded fields.
valid_decode_line:
    xor eax, eax
    cmp edx, 25
    jb .end
    cmp edx, 49
    ja .end
    cmp word [rcx+42], '~'
    je .chars
    cmp word [rcx+42], ':'
    je .chars
    cmp word [rcx+42], '+'
    jne .end
.chars:
    movzx r8d, word [rcx]
    cmp r8d, ' '
    jb .end
    cmp r8d, '~'
    ja .end
    add rcx, 2
    dec edx
    jnz .chars
    inc eax
.end:
    ret

control_packet:
    ; Validate this packet without affecting Qt's negotiated stream schema.
    cmp edx, 21
    jne .end
    cmp dword [rcx], 0xdacbbcAD
    jne .end
    cmp dword [rcx+4], 0x02000000
    je .id
    cmp dword [rcx+4], 0x03000000
    jne .end
.id:
    cmp dword [rcx+12], 0x04000000
    jne .end
    cmp dword [rcx+16], 0x5844544a
    jne .end
    cmp byte [rcx+20], 0
    je .release
    cmp byte [rcx+20], 15
    ja .end
    test byte [rcx+20], 12
    jz .valid
    test byte [rcx+20], 2
    jz .end
    test byte [rcx+20], 4
    jz .valid
    test byte [rcx+20], 8
    jz .end
.valid:
    ; The builder supplies unwind metadata for the clock-calling helper.
    jmp renew_lease
.release:
    lea rax, [rel lease_expiry]
    mov qword [rax], 0
    mov byte [rel lease_flags], 0
.end:
    ret

renew_lease:
    sub rsp, 0x28
    movzx eax, byte [rcx+20]
    mov [rsp+0x20], eax
renew_call:
    call_tick
renew_return:
    mov ecx, [rsp+0x20]
    test cl, 2
    jz .update
    test byte [rel lease_flags], 2
    jz .new_session
    state_load {0x48,0x3b,0x05}, select_until
    jb .update
.new_session:
    inc dword [rel report_epoch]
    mov qword [rel last_report], 0
.update:
    add rax, 2500
    lea rdx, [rel lease_expiry]
    mov [rdx], rax
    mov eax, [rsp+0x20]
    mov [rel lease_flags], al
    add rsp, 0x28
    ret
renew_end:

resolve_tick:
    state_load {0x48,0x8b,0x05}, tick_function
    test rax, rax
    jne resolve_return
    sub rsp, 0x28
resolve_body:
    lea rcx, [rel kernel_dll]
    iat_call get_module
    test rax, rax
    jz .restore
    mov rcx, rax
    lea rdx, [rel clock_export]
    iat_call get_symbol
    state_load {0x48,0x89,0x05}, tick_function
.restore:
    add rsp, 0x28
resolve_return:
    ret
resolve_end:

; Leaf wrappers are executed inside the original process_Auto stack frame.
; An active lease skips only the new-station selector. It neither changes
; the menu nor impersonates the SingleShot setting during a QSO.
select_station:
    test byte [rel lease_flags], 1
    jz .original
    state_load {0x48,0x8b,0x0d}, select_until
    test rcx, rcx
    jz .original
    mov [rsp+0x30], rax
    mov [rsp+0x38], rbp
    mov [rsp+0x28], r10
    call_tick
    mov r10, [rsp+0x28]
    state_load {0x48,0x3b,0x05}, select_until
    mov rax, [rsp+0x30]
    jb .managed
.original:
    cmp byte [rbx+0x68b], 0
    jne 0x1400a8c60
    jmp 0x1400a8991
.managed:
    ; r10b is the original 'counters' flag. Keep normal CQ idle state,
    ; but take the recognized halt path if this exchange ran out of tries.
    test r10b, r10b
    jnz 0x1400a8c60
    mov rbp, [rsp+0x38]
    jmp managed_counter_stop

after_counters:
    test byte [rel lease_flags], 1
    jz after_counters_original
    state_load {0x48,0x8b,0x05}, select_until
    test rax, rax
    jz after_counters_original
    call_tick
    state_load {0x48,0x3b,0x05}, select_until
    jae after_counters_original
managed_counter_stop:
    ; Preserve the recognized reason for exhausted native QSO counters.
    mov rcx, rsi
    lea rdx, [rel auto_counter_reason]
    call qstring_from_c
    mov rdx, rsi
    mov rcx, rbx
    call auto_stop
    jmp auto_cleanup
counter_stop_end:
after_counters_original:
    cmp byte [rbx+0x686], 0
    jne 0x1400aa470
    jmp 0x1400a8ed2

; Reads the lease on a controlled process_Auto fixture, as well as providing
; the exact comparison used by the harness for timeout and release checks.
select_lease:
    xor eax, eax
    test byte [rel lease_flags], 1
    jz .return
    state_load {0x48,0x3b,0x0d}, select_until
    setb al
.return:
    ret

; guiUpdate is about to assert PTT. In FT8/FT4, wait for the decoder
; during addressed AutoCQ exchanges or before a native terminal repeat.
; The ordinary timing checks apply again on the next GUI tick.
defer_ptt:
    cmp byte [rbx+0x2e45], 0      ; tune
    jne .start
    mov rax, [rbx+0x2ae8]        ; mode QString
    cmp dword [rax+4], 3
    jne .start
    add rax, [rax+16]
    cmp dword [rax], 0x00540046   ; "FT" in UTF-16
    jne .start
    cmp word [rax+4], '8'
    je .digital
    cmp word [rax+4], '4'
    jne .start
.digital:
    ; During a managed sign-off, key only when the companion permits
    ; a repeat. While waiting for its packet, keep the slot available.
    test byte [rel lease_flags], 8
    jz .busy
    cmp byte [rbx+0x68b], 0
    jne .busy
    mov rax, [rbx+0x2a60]
    cmp dword [rax+4], 0
    je .busy
    call_tick
    state_load {0x48,0x3b,0x05}, select_until
    jae .busy
    test byte [rel lease_flags], 4
    jz .wait
.busy:
    cmp byte [rbx+0x5f3], 0       ; decoder busy
    je .start
    mov eax, [rbx+0x4f4]         ; selected transmit message
    sub eax, 4
    cmp eax, 1
    jbe .terminal
    cmp eax, 3                  ; generated message from the second UI tab
    jne .check_managed
    cmp dword [rbx+0x768], 4
    je .terminal
    cmp dword [rbx+0x768], 5
    jne .check_managed
.terminal:
    cmp byte [rbx+0x653], 0      ; native terminal messages also wait in ordinary AutoSeq
    jne .wait
.check_managed:
    mov rax, [rbx+0x2a60]        ; current DX QString
    cmp dword [rax+4], 0
    je .start
    test byte [rel lease_flags], 2
    jz .start
    call_tick
    state_load {0x48,0x3b,0x05}, select_until
    jae .start
.wait:
    jmp 0x14010ee50
.start:
    mov eax, [rbx+0x548]
    jmp 0x14010eef7

; The native limit is already reached at these sites. With AutoCQ
; active, use its recognized halt path and let the module choose
; the next station after the current RX cycle.
%macro counter_hook 3
%1:
    test byte [rel lease_flags], 2
    jz %%original
    call_tick
    state_load {0x48,0x3b,0x05}, select_until
    jb managed_counter_stop
%%original:
    db 0x48, 0x8d, 0x15          ; lea rdx, [original reason]
    dd %2 - ($ + 4)
    jmp %3
%endmacro
counter_hook stop_cq_retry, 0x1413572f0, 0x1400ab027
counter_hook stop_call_retry, 0x141357320, 0x1400ab271
counter_hook stop_report_retry, 0x141357348, 0x1400ab4e7

; process_Auto uses these branches after consulting the active QSO history.
; With the AutoCQ lease, the companion owns the RR73 retry count and
; checks for fresh peer acknowledgements before allowing another 73.
%macro terminal_hook 3
%1:
    test byte [rel lease_flags], 2
    jz .original
    cmp byte [rbx+0x68b], 0      ; leave DXpedition/Hound sequencing to JTDX
    jne .original
    call_tick
    state_load {0x48,0x3b,0x05}, select_until
    jae .original
    %2
.original:
    %3
%endmacro

%macro finish_rr73 0
    jmp 0x1400a8f08             ; normal QString cleanup, no halt and no DX clear
%endmacro
%macro original_rr73_stop 0
    cmp byte [rbx+0x686], 0
    je 0x1400aa640
    jmp 0x1400a9e8a
%endmacro
terminal_hook skip_early_rr73_stop, finish_rr73, original_rr73_stop

%macro skip_terminal_clear 0
    jmp 0x1400a9d00
%endmacro
%macro original_terminal_clear 0
    cmp byte [rbx+0x5f9], 0
    jne 0x1400ab20e
    jmp 0x1400a8de3
%endmacro
terminal_hook keep_terminal_counter, skip_terminal_clear, original_terminal_clear

%macro check_reverse_ack 0
    test byte [rel lease_flags], 8
    jnz 0x1400a8f08
%endmacro
%macro original_fin 0
    cmp byte [rbx+0x686], 0
    je 0x1400aa753
    jmp 0x1400a9e5e
%endmacro
terminal_hook repeat_final_73, check_reverse_ack, original_fin

%macro original_s73 0
    lea rdx, [rel s73_reason]
    jmp 0x1400a9f50
%endmacro
terminal_hook hold_s73, check_reverse_ack, original_s73

; guiUpdate has already recognized a sign-off. These two original QString
; comparisons identify a call still in the recent log. A new exchange can
; use the same call, so for FT8/FT4 consult JTDX's own history for that call.
first_recent_log_gate:
    test al, al
    jz 0x140110dd0              ; check the second most recently logged call
    jmp check_recent_exchange

second_recent_log_gate:
    test al, al
    jz 0x140110de9              ; continue with the previous-message check
check_recent_exchange:
    cmp byte [rbx+0x68b], 0     ; Hound/DXpedition
    jne .duplicate
    mov rax, [rbx+0x2ae8]       ; mode QString
    cmp dword [rax+4], 3
    jne .duplicate
    add rax, [rax+16]
    cmp dword [rax], 0x00540046 ; "FT", in UTF-16
    jne .duplicate
    cmp word [rax+4], '8'
    je .lookup
    cmp word [rax+4], '4'
    jne .duplicate
.lookup:
    mov rcx, rbx
    call new_exchange_in_history
    test eax, eax
    jnz 0x140110de9
.duplicate:
    jmp 0x14010f4cc             ; original duplicate sign-off path

; QsoHistory::log_data supplies the active callsign's start in UTC seconds
; of day. Use the full QDateTime that process_Auto saved for this exchange
; only when its time matches the history entry. Retransmissions keep the
; exchange's original start. A fresh start lies after the last actual log.
new_exchange_in_history:
    sub rsp, 0x58
    mov [rsp+0x30], rcx         ; MainWindow
    mov rax, [rcx+0x2a60]       ; DX QString, as in the native log QSO slot
    cmp dword [rax+4], 0
    je .no
    iat_load qstring_null
    mov [rsp+0x38], rax         ; received report QString
    mov [rsp+0x40], rax         ; sent report QString
    lea r9, [rsp+0x38]
    lea rax, [rsp+0x40]
    mov [rsp+0x20], rax
    lea r8, [rsp+0x48]
    mov dword [r8], -1          ; no start time supplied yet
    lea rdx, [rcx+0x2a60]
    lea rcx, [rcx+0x2b70]       ; QsoHistory
    call qso_log_data
    mov [rsp+0x4c], eax
    lea rcx, [rsp+0x38]
    call qstring_destroy
    lea rcx, [rsp+0x40]
    call qstring_destroy
    cmp dword [rsp+0x4c], 7     ; need report exchange, as in native QSO logging
    jle .no
    cmp dword [rsp+0x48], 86400
    jae .no
    mov rcx, [rsp+0x30]
    add rcx, 0x2b58             ; m_dateTimeQSOOn
    iat_call date_time_ms
    test rax, rax
    jle .no
    mov [rsp+0x50], rax
    xor edx, edx
    mov ecx, 86400000
    div rcx                     ; EDX is the start's millisecond of day
    imul eax, dword [rsp+0x48], 1000
    cmp edx, eax
    jne .no
    mov rcx, [rsp+0x30]
    add rcx, 0x760              ; most recent log QDateTime
    iat_call date_time_ms
    test rax, rax
    jle .no
    cmp rax, [rsp+0x50]
    setl al
    movzx eax, al
    jmp .done
.no:
    xor eax, eax
.done:
    add rsp, 0x58
    ret
new_exchange_end:

; Both guiUpdate paths have just assigned a report from a TX message.
remember_roger_report:
    jmp remember_report
remember_report:
    mov rcx, rbx
    xor edx, edx                ; record a report
    call track_report
    jmp 0x14010e873

; The auto-log can resume a contact after sending a report to another
; station. Use the saved value for this DX instead of the latest global
; report or a receive estimate from a different exchange.
log_with_sent_report:
    call qso_log_data
    cmp eax, 7
    jle .resume
    mov [rsp+0x20], eax         ; fifth argument slot after log_data
    mov rcx, r14                ; MainWindow
    mov rdx, rdi                ; sent report QString from history
    lea r8, [rsp+0xbc]          ; native start in UTC seconds of day
    call track_report
    mov eax, [rsp+0x20]
.resume:
    jmp 0x1400e8ee1

track_report:
    sub rsp, 0x68
    test byte [rel lease_flags], 2
    jz .done
    cmp byte [rcx+0x68b], 0      ; Hound/DXpedition
    jne .done
    test rdx, rdx
    jz .mode
    cmp byte [rcx+0x65f], 0      ; automatic log on sign-off
    je .done
.mode:
    mov rax, [rcx+0x2ae8]        ; mode QString
    cmp dword [rax+4], 3
    jne .done
    add rax, [rax+16]
    cmp dword [rax], 0x00540046  ; "FT" in UTF-16
    jne .done
    movzx r9d, word [rax+4]
    cmp r9d, '8'
    je .check_lease
    cmp r9d, '4'
    jne .done
.check_lease:
    mov [rsp+0x20], rcx
    mov [rsp+0x28], rdx
    mov [rsp+0x30], r8
    mov [rsp+0x52], r9w
    call_tick
    state_load {0x48,0x3b,0x05}, select_until
    jae .done
    mov rcx, [rsp+0x20]
    mov rax, [rcx+0x340]         ; dial frequency
    mov [rsp+0x48], rax
    mov r8, [rcx+0x2a60]         ; active DX QString
    mov edx, [r8+4]
    lea eax, [rdx-3]
    cmp eax, 13
    ja .done
    mov [rsp+0x50], dx
    add r8, [r8+16]
    mov [rsp+0x40], r8
    cmp qword [rsp+0x28], 0
    jne .timestamp

    ; The report assignment already validates the sender and payload.
    ; Verify that the first word of the generated TX is this exact DX.
    mov r9, [rcx+0x628]
    cmp [r9+4], edx
    jle .done
    add r9, [r9+16]
    cmp word [r9+rdx*2], ' '
    jne .done
    xor eax, eax
.compare_call:
    mov r10w, [r8+rax*2]
    cmp r10w, [r9+rax*2]
    jne .done
    inc eax
    cmp eax, edx
    jb .compare_call
    mov r9, [rcx+0x2b10]         ; m_rptSent QString
    cmp dword [r9+4], 3
    jne .done
    add r9, [r9+16]
    movzx eax, word [r9]
    cmp eax, '-'
    je .digits
    cmp eax, '+'
    jne .done
.digits:
    movzx edx, word [r9+2]
    sub edx, '0'
    cmp edx, 9
    ja .done
    movzx edx, word [r9+4]
    sub edx, '0'
    cmp edx, 9
    ja .done
    movzx r10d, byte [r9+2]
    shl r10d, 8
    or eax, r10d
    shl edx, 16
    or eax, edx
    add eax, '0' << 16
    mov [rsp+0x54], eax
.timestamp:
    mov rcx, [rcx+0x38]          ; JTDX clock, including its configured offset
    call radio_time_ms
    test rax, rax
    jle .done
    mov [rsp+0x38], rax
    mov r10, [rsp+0x48]
    mov r11d, [rsp+0x50]
    mov rcx, [rsp+0x40]
    lea r8, [rel reports]
    mov r9d, report_rows
.lookup:
    cmp r10, [r8+16]
    jne .next
    cmp r11d, [r8+24]
    jne .next
    mov edx, [rel report_epoch]
    cmp edx, [r8+28]
    jne .next
    mov rax, [rsp+0x38]
    sub rax, [r8]
    cmp rax, report_lifetime
    ja .next
    movzx eax, r11w
.compare_saved:
    dec eax
    mov dx, [rcx+rax*2]
    cmp dx, [r8+rax*2+32]
    jne .next
    test eax, eax
    jnz .compare_saved
    cmp qword [rsp+0x28], 0
    jne .log_report
    cmp r8, [rel last_report]
    jne .new_start
    mov rax, [rsp+0x38]
    sub rax, [r8]
    cmp rax, 32000               ; consecutive reports of this attempt
    jbe .save
    jmp .new_start
.next:
    add r8, report_size
    dec r9d
    jnz .lookup
    cmp qword [rsp+0x28], 0
    jne .done
    mov eax, [rel report_slot]
    lea edx, [rax+1]
    cmp edx, report_rows
    jb .slot
    xor edx, edx
.slot:
    mov [rel report_slot], edx
    shl eax, 6
    lea r8, [rel reports]
    add r8, rax
    mov [r8+16], r10
    mov [r8+24], r11d
    mov edx, [rel report_epoch]
    mov [r8+28], edx
    movzx eax, r11w
.save_call:
    dec eax
    mov dx, [rcx+rax*2]
    mov [r8+rax*2+32], dx
    test eax, eax
    jnz .save_call
.new_start:
    mov rax, [rsp+0x38]
    xor edx, edx
    mov ecx, 86400000
    div rcx
    mov eax, edx
    xor edx, edx
    mov ecx, 1000
    div ecx
    mov [r8+8], eax
.save:
    mov rax, [rsp+0x38]
    mov [r8], rax
    mov eax, [rsp+0x54]
    mov [r8+12], eax
    mov [rel last_report], r8
    jmp .done
.log_report:
    mov rdx, [rsp+0x30]
    cmp dword [rdx], 0
    je .restore_start
    cmp r8, [rel last_report]
    jne .restore_start
    mov rax, [rsp+0x38]
    sub rax, [r8]
    cmp rax, 46000
    jbe .restore_report
.restore_start:
    mov eax, [r8+8]
    mov [rdx], eax
.restore_report:
    mov qword [r8], 0            ; do not reuse a completed exchange
    lea rcx, [rsp+0x58]
    lea rdx, [r8+12]
    call qstring_from_c
    mov rcx, [rsp+0x28]
    lea rdx, [rsp+0x58]
    iat_call qstring_assign
    lea rcx, [rsp+0x58]
    call qstring_destroy
.done:
    add rsp, 0x68
    ret
track_report_end:

network_dll: db 'Qt5Network.dll', 0
ipv4_export: db '_ZNK12QHostAddress13toIPv4AddressEPb', 0
kernel_dll: db 'kernel32.dll', 0
clock_export: db 'GetTickCount64', 0
auto_counter_reason: db 'AutoCQ, counter triggered ', 0
s73_reason: db 'S73, none received ', 0
lease_expiry equ $$ + (select_until - 0x142d17000)
text_section_end:
