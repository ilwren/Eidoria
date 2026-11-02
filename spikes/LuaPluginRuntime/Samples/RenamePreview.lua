-- Built-in demo: generate a rename proposal only; the script cannot rename files.
local count = eidoria_file_count
if count == 0 then
    return eidoria_preview("序号重命名预览", "report-only", "没有匹配文件")
end
return eidoria_preview("序号重命名预览", "rename-preview", "文件名-001.ext；逐项确认后才可应用")
