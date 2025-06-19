using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace super_toolbox
{
    public partial class SuperToolbox : Form
    {
        private int totalFileCount;
        private Dictionary<string, TreeNode> formatNodes = new Dictionary<string, TreeNode>();
        private Dictionary<string, TreeNode> categoryNodes = new Dictionary<string, TreeNode>();
        private readonly List<string>[] messageBuffers = new List<string>[2];
        private readonly object[] bufferLocks = { new object(), new object() };
        private int activeBufferIndex;
        private bool isUpdatingUI;
        private System.Windows.Forms.Timer updateTimer;
        private CancellationTokenSource extractionCancellationTokenSource;
        private const int UpdateInterval = 200;
        private const int MaxMessagesPerUpdate = 1000;
        private bool isExtracting;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel lblStatus;
        private ToolStripStatusLabel lblFileCount;
        private readonly Dictionary<string, string> defaultCategories = new Dictionary<string, string>
        {
            { "RIFF - wave系列[需要ffmpeg]", "音频" },
            { "RIFF - Fmod - bank", "音频" },
            { "RIFF - wmav2 - xwma", "音频" },
            { "RIFX - BigEndian - wem", "音频" },
            { "RIFF - cdxa - xa", "音频" },
            { "CRI - adpcm_adx - adx", "音频" },
            { "CRI - adpcm_adx - ahx", "音频" },
            { "Fmod - fsb5", "音频" },
            { "Xiph.Org - Ogg", "音频" },
            { "CRI - HCA - hca", "音频" },
            { "任天堂 - libopus - lopus", "音频" },
            { "光荣特库摩 - sound", "音频" },
            { "RIFF - Google - webp", "图片" },
            { "JPEG/JPG", "图片" },
            { "PNG", "图片" },
            { "ENDILTLE - APK - apk", "其他档案" },
            { "东方天空竞技场 - GPK - gpk", "其他档案" },
            { "GxArchivedFile - dat", "其他档案"},
            { "苍之彼方的四重奏EXTRA2 - dat", "其他档案" },
            { "Lightvn galgame engine - mcdat/vndat", "其他档案" }
        };

        public SuperToolbox()
        {
            InitializeComponent();
            statusStrip1 = new StatusStrip();
            lblStatus = new ToolStripStatusLabel { Text = "就绪" };
            lblFileCount = new ToolStripStatusLabel { Text = "已提取: 0 个文件" };
            statusStrip1.Items.Add(lblStatus);
            statusStrip1.Items.Add(lblFileCount);
            this.Controls.Add(statusStrip1);

            InitializeTreeView();
            messageBuffers[0] = new List<string>(MaxMessagesPerUpdate);
            messageBuffers[1] = new List<string>(MaxMessagesPerUpdate);
            updateTimer = new System.Windows.Forms.Timer { Interval = UpdateInterval };
            updateTimer.Tick += UpdateUITimerTick;
            updateTimer.Start();
            extractionCancellationTokenSource = new CancellationTokenSource();
        }

        private void InitializeTreeView()
        {
            foreach (string category in defaultCategories.Values.Distinct())
            {
                AddCategory(category);
            }
            foreach (var item in defaultCategories)
            {
                string extractorName = item.Key;
                string categoryName = item.Value;
                TreeNode categoryNode = categoryNodes[categoryName];
                TreeNode extractorNode = categoryNode.Nodes.Add(extractorName);
                formatNodes[extractorName] = extractorNode;
                extractorNode.Tag = extractorName;
            }
            treeView1.ExpandAll();
        }

        private TreeNode AddCategory(string categoryName)
        {
            if (categoryNodes.ContainsKey(categoryName)) return categoryNodes[categoryName];
            TreeNode categoryNode = treeView1.Nodes.Add(categoryName);
            categoryNode.Tag = "category";
            categoryNodes[categoryName] = categoryNode;
            return categoryNode;
        }

        private void btnSelectFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
            {
                folderBrowserDialog.Description = "选择要提取的文件夹";
                folderBrowserDialog.ShowNewFolderButton = false;

                string inputPath = txtFolderPath.Text;
                if (!string.IsNullOrEmpty(inputPath) && Directory.Exists(inputPath))
                {
                    folderBrowserDialog.SelectedPath = inputPath;
                }

                if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
                {
                    txtFolderPath.Text = folderBrowserDialog.SelectedPath;
                    EnqueueMessage($"已选择文件夹: {folderBrowserDialog.SelectedPath}");
                }
            }
        }

        private async void btnExtract_Click(object sender, EventArgs e)
        {
            if (isExtracting)
            {
                EnqueueMessage("正在进行提取操作，请等待...");
                return;
            }
            string dirPath = txtFolderPath.Text;
            if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath))
            {
                EnqueueMessage($"错误: {dirPath} 不是一个有效的目录。");
                return;
            }
            TreeNode selectedNode = treeView1.SelectedNode;
            if (selectedNode == null || selectedNode.Tag as string == "category")
            {
                EnqueueMessage("请选择一个具体的文件格式。");
                return;
            }
            string formatName = selectedNode.Text;
            totalFileCount = 0;
            isExtracting = true;
            UpdateUIState(true);
            try
            {
                var extractor = CreateExtractor(formatName);
                if (extractor == null)
                {
                    EnqueueMessage($"错误: 不支持的格式 {formatName}");
                    isExtracting = false;
                    UpdateUIState(false);
                    return;
                }
                EnqueueMessage($"开始提取 {formatName} 格式的文件...");
                var fileExtractedEventInfo = extractor.GetType().GetEvent("FileExtracted");
                var extractionProgressEventInfo = extractor.GetType().GetEvent("ExtractionProgress");
                var filesExtractedEventInfo = extractor.GetType().GetEvent("FilesExtracted");
                if (fileExtractedEventInfo != null)
                {
                    fileExtractedEventInfo.AddEventHandler(extractor, new EventHandler<string>((s, fileName) =>
                    {
                        Interlocked.Increment(ref totalFileCount);
                        EnqueueMessage($"已提取: {Path.GetFileName(fileName)}");
                    }));
                }
                if (extractionProgressEventInfo != null)
                {
                    extractionProgressEventInfo.AddEventHandler(extractor, new EventHandler<string>((s, message) =>
                    {
                        EnqueueMessage(message);
                    }));
                }
                if (filesExtractedEventInfo != null)
                {
                    filesExtractedEventInfo.AddEventHandler(extractor, new EventHandler<List<string>>((s, fileNames) =>
                    {
                        foreach (var fileName in fileNames)
                        {
                            Interlocked.Increment(ref totalFileCount);
                            EnqueueMessage($"已提取: {Path.GetFileName(fileName)}");
                        }
                    }));
                }
                await Task.Run(async () =>
                {
                    try
                    {
                        await extractor.ExtractAsync(dirPath, extractionCancellationTokenSource.Token);
                        this.Invoke(new Action(() =>
                        {
                            UpdateFileCountDisplay();
                            EnqueueMessage($"提取操作完成，总共提取了 {totalFileCount} 个文件");
                        }));
                    }
                    catch (OperationCanceledException)
                    {
                        this.Invoke(new Action(() =>
                        {
                            EnqueueMessage("提取操作已取消");
                        }));
                    }
                    catch (Exception ex)
                    {
                        this.Invoke(new Action(() =>
                        {
                            EnqueueMessage($"提取过程中出现错误: {ex.Message}");
                        }));
                    }
                    finally
                    {
                        this.Invoke(new Action(() =>
                        {
                            isExtracting = false;
                            UpdateUIState(false);
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                EnqueueMessage($"提取过程中出现错误: {ex.Message}");
                isExtracting = false;
                UpdateUIState(false);
            }
        }

        private BaseExtractor CreateExtractor(string formatName)
        {
            switch (formatName)
            {
                case "RIFF - wave系列[需要ffmpeg]": return new WaveExtractor();
                case "RIFF - Fmod - bank": return new BankExtractor();
                case "RIFF - Google - webp": return new WebpExtractor();
                case "RIFF - wmav2 - xwma": return new XwmaExtractor();
                case "RIFX - BigEndian - wem": return new RifxExtractor();
                case "RIFF - cdxa - xa": return new CdxaExtractor();
                case "CRI - adpcm_adx - adx": return new AdxExtractor();
                case "CRI - adpcm_adx - ahx": return new AhxExtractor();
                case "Fmod - fsb5": return new Fsb5Extractor();
                case "任天堂 - libopus - lopus": return new LopusExtractor();
                case "光荣特库摩 - sound": return new Kvs_Kns_Extractor();
                case "Xiph.Org - Ogg": return new OggExtractor();
                case "JPEG/JPG": return new JpgExtractor();
                case "PNG": return new PngExtractor();
                case "CRI - HCA - hca": return new HcaExtractor();
                case "ENDILTLE - APK -apk": return new ApkExtractor();
                case "东方天空竞技场 - GPK -gpk": return new GpkExtractor();
                case "GxArchivedFile - dat": return new DatExtractor();
                case "苍之彼方的四重奏EXTRA2 - dat": return new Aokana2Extractor();
                case "Lightvn galgame engine - mcdat/vndat": return new LightvnExtractor();
                default: throw new NotSupportedException($"不支持的格式: {formatName}");
            }
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            lock (bufferLocks[0]) { messageBuffers[0].Clear(); }
            lock (bufferLocks[1]) { messageBuffers[1].Clear(); richTextBox1.Clear(); }
            totalFileCount = 0;
            UpdateFileCountDisplay();
        }

        private void EnqueueMessage(string message)
        {
            int bufferIndex = activeBufferIndex;
            lock (bufferLocks[bufferIndex])
            {
                if (messageBuffers[bufferIndex].Count >= MaxMessagesPerUpdate && !isUpdatingUI)
                {
                    activeBufferIndex = (activeBufferIndex + 1) % 2;
                    bufferIndex = activeBufferIndex;
                }
                messageBuffers[bufferIndex].Add(message);
            }
        }

        private void UpdateUITimerTick(object? sender, EventArgs e)
        {
            if (isUpdatingUI) return;
            int inactiveBufferIndex = (activeBufferIndex + 1) % 2;
            object bufferLock = bufferLocks[inactiveBufferIndex];
            List<string>? messagesToUpdate = null;
            lock (bufferLock)
            {
                if (messageBuffers[inactiveBufferIndex].Count > 0)
                {
                    isUpdatingUI = true;
                    messagesToUpdate = new List<string>(messageBuffers[inactiveBufferIndex]);
                    messageBuffers[inactiveBufferIndex].Clear();
                }
            }
            if (messagesToUpdate != null && messagesToUpdate.Count > 0) UpdateRichTextBox(messagesToUpdate);
            else isUpdatingUI = false;
        }

        private void UpdateRichTextBox(List<string> messages)
        {
            if (richTextBox1.IsDisposed || richTextBox1.Disposing) { isUpdatingUI = false; return; }
            if (richTextBox1.InvokeRequired)
            {
                try { richTextBox1.Invoke(new Action(() => UpdateRichTextBoxInternal(messages))); }
                catch { isUpdatingUI = false; return; }
            }
            else UpdateRichTextBoxInternal(messages);
        }

        private void UpdateRichTextBoxInternal(List<string> messages)
        {
            if (statusStrip1 == null || lblFileCount == null) return;
            try
            {
                richTextBox1.SuspendLayout();
                StringBuilder sb = new StringBuilder();
                foreach (string message in messages) sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
                int scrollPosition = richTextBox1.SelectionStart;
                bool isAtBottom = scrollPosition >= richTextBox1.TextLength - 10;
                richTextBox1.AppendText(sb.ToString());
                if (isAtBottom) richTextBox1.ScrollToCaret();
                else { richTextBox1.SelectionStart = scrollPosition; richTextBox1.SelectionLength = 0; }
            }
            catch { }
            finally { richTextBox1.ResumeLayout(); isUpdatingUI = false; }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node != null && lblStatus != null)
            {
                string nodeType = e.Node.Tag as string == "category" ? "分组" : "提取器";
                lblStatus.Text = $"已选择: {e.Node.Text} ({nodeType})";
            }
        }

        private void treeViewContextMenu_Opening(object sender, CancelEventArgs e)
        {
            if (treeView1.SelectedNode == null)
            {
                e.Cancel = false;
                moveToCategoryMenuItem.Visible = false;
                renameCategoryMenuItem.Visible = false;
                deleteCategoryMenuItem.Visible = false;
                addNewCategoryMenuItem.Visible = true;
                return;
            }
            bool isCategory = treeView1.SelectedNode.Tag as string == "category";
            moveToCategoryMenuItem.Visible = !isCategory;
            renameCategoryMenuItem.Visible = isCategory;
            deleteCategoryMenuItem.Visible = isCategory && treeView1.SelectedNode.Nodes.Count == 0 &&
                !defaultCategories.Values.Contains(treeView1.SelectedNode.Text);
            addNewCategoryMenuItem.Visible = true;
            moveToCategoryMenuItem.DropDownItems.Clear();
            if (!isCategory)
            {
                foreach (string category in categoryNodes.Keys)
                {
                    ToolStripMenuItem item = new ToolStripMenuItem(category);
                    item.Click += (s, args) => MoveSelectedNodeToCategory(category);
                    moveToCategoryMenuItem.DropDownItems.Add(item);
                }
            }
        }

        private void MoveSelectedNodeToCategory(string category)
        {
            TreeNode selectedNode = treeView1.SelectedNode;
            if (selectedNode == null || selectedNode.Parent == null || selectedNode.Tag as string == "category") return;
            TreeNode? targetCategory = categoryNodes.ContainsKey(category) ? categoryNodes[category] : null;
            if (targetCategory == null || selectedNode.Parent == targetCategory) return;
            selectedNode.Remove();
            targetCategory.Nodes.Add(selectedNode);
            treeView1.SelectedNode = selectedNode;
            EnqueueMessage($"已将 {selectedNode.Text} 移动到 {category} 分组");
        }

        private void UpdateUIState(bool isExtracting)
        {
            btnExtract.Enabled = !isExtracting;
            btnSelectFolder.Enabled = !isExtracting;
            treeView1.Enabled = !isExtracting;
            if (lblStatus != null) lblStatus.Text = isExtracting ? "正在提取..." : "就绪";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                updateTimer?.Stop();
                updateTimer?.Dispose();
                extractionCancellationTokenSource?.Cancel();
                extractionCancellationTokenSource?.Dispose();
            }
            catch { }

            base.OnFormClosing(e);
        }

        private void UpdateFileCountDisplay()
        {
            if (lblFileCount != null) lblFileCount.Text = $"已提取: {totalFileCount} 个文件";
        }

        private string ShowInputDialog(string title, string prompt, string initialValue = "")
        {
            string result = string.Empty;

            Form dialog = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(330, 130)
            };

            Label label = new Label
            {
                Text = prompt,
                Location = new Point(20, 20),
                AutoSize = true
            };

            TextBox textBox = new TextBox
            {
                Text = initialValue,
                Location = new Point(20, 45),
                Size = new Size(285, 23),
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };

            Button okButton = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(140, 80),
                Size = new Size(75, 23)
            };

            Button cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(230, 80),
                Size = new Size(75, 23)
            };

            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;

            dialog.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                result = textBox.Text ?? string.Empty;
            }

            return result;
        }

        private void addNewCategoryMenuItem_Click(object sender, EventArgs e)
        {
            string categoryName = ShowInputDialog("添加新分组", "请输入分组名称:");
            if (!string.IsNullOrEmpty(categoryName))
            {
                if (string.IsNullOrEmpty(categoryName.Trim()))
                {
                    MessageBox.Show("分组名称不能为空！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (categoryNodes.ContainsKey(categoryName))
                {
                    MessageBox.Show($"分组 '{categoryName}' 已存在！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                TreeNode newCategory = AddCategory(categoryName);
                treeView1.SelectedNode = newCategory;
                treeView1.ExpandAll();
                EnqueueMessage($"已添加新分组: {categoryName}");
            }
        }

        private void renameCategoryMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode selectedNode = treeView1.SelectedNode;
            if (selectedNode == null || selectedNode.Tag as string != "category")
            {
                MessageBox.Show("请选择一个分组进行编辑！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (defaultCategories.Values.Contains(selectedNode.Text))
            {
                MessageBox.Show("不能编辑默认分组！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string newName = ShowInputDialog("编辑分组", "请输入新的分组名称:", selectedNode.Text);
            if (!string.IsNullOrEmpty(newName))
            {
                if (string.IsNullOrEmpty(newName.Trim()))
                {
                    MessageBox.Show("分组名称不能为空！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (categoryNodes.ContainsKey(newName))
                {
                    MessageBox.Show($"分组 '{newName}' 已存在！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string oldName = selectedNode.Text;
                categoryNodes.Remove(oldName);
                selectedNode.Text = newName;
                categoryNodes[newName] = selectedNode;
                EnqueueMessage($"已将分组 '{oldName}' 重命名为: {newName}");
            }
        }

        private void deleteCategoryMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode selectedNode = treeView1.SelectedNode;
            if (selectedNode == null || selectedNode.Tag as string != "category")
            {
                MessageBox.Show("请选择一个分组进行删除！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (selectedNode.Nodes.Count > 0)
            {
                MessageBox.Show("无法删除非空分组，请先将其中的提取器移至其他分组！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (defaultCategories.Values.Contains(selectedNode.Text))
            {
                MessageBox.Show("不能删除默认分组！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show($"确定要删除分组 '{selectedNode.Text}' 吗？", "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                string categoryName = selectedNode.Text;
                selectedNode.Remove();
                categoryNodes.Remove(categoryName);
                EnqueueMessage($"已删除分组: {categoryName}");
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
        }
    }
}
