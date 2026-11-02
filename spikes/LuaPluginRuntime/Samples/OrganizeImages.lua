-- Built-in demo: the host provides metadata only. No path or file API is exposed.
local count = eidoria_file_count
if count == 0 then
    return eidoria_preview("图片整理", "report-only", "没有待处理图片")
end
return eidoria_preview("图片整理", "copy-preview", "图片整理/（等待用户确认）")
