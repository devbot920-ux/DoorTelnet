-- Sample automation script for DoorTelnet
print("Sample script loaded")

onConnect(function()
  print("Connected! You can type your credentials.")
  -- Removed automatic guest send to avoid duplicate inputs
end)

onTick(function()
  -- Periodic debug (disabled)
end)

-- Example pattern auto-response (disabled to prevent auto '1' spam)
-- onMatch("Welcome", function()
--   print("Matched Welcome banner")
--   send("1")
-- end)
